using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WinStream.Audio;

namespace WinStream.Network
{
    public sealed class AirPlay2MediaSetupResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public int DataPort { get; init; }
        public int ControlPort { get; init; }
        public int TimingPort { get; init; }
        public int AudioLatency { get; init; } = 11025;
        public AudioCodec AudioCodec { get; init; } = AudioCodec.AirPlay2AacEld;
        public byte[] AudioAesKey { get; init; } = Array.Empty<byte>();
        public byte[] AudioAesIv { get; init; } = Array.Empty<byte>();
    }

    internal static class AirPlay2MediaSetupService
    {
        private const int LocalControlPort = 6001;
        private const int LocalTimingPort = 6002;
        private const string MediaKeyHexEnvVar = "WINSTREAM_AIRPLAY2_MEDIA_KEY_HEX";
        private const string BinaryPlistContentType = "application/x-apple-binary-plist";
        private const string OctetStreamContentType = "application/octet-stream";

        // audioFormat bitmask values per AirPlay 2 spec
        // (Emanuele Cozzi / openairplay documentation)
        private const ulong AudioFormatPcm44100Stereo = 0x200UL;   // PCM L16 44.1 kHz stereo
        private const ulong AudioFormatAlac44100 = 0x1000UL;        // ALAC 44.1 kHz 16-bit
        private const ulong AudioFormatAacEld44100 = 0x40000UL;     // AAC-ELD 44.1 kHz

        public static async Task<AirPlay2MediaSetupResult> SetupAudioSessionAsync(
            RtspClient rtspClient,
            DeviceInfo deviceInfo,
            AirPlay2AuthResult authResult)
        {
            try
            {
                await SendInfoProbeAsync(rtspClient);

                var fairPlayResult = await RunFairPlaySetupAsync(rtspClient, authResult);
                Logger.LogMessage(
                    fairPlayResult.Success
                        ? "AirPlay 2 fp-setup succeeded; using FairPlay audio encryption context."
                        : $"AirPlay 2 fp-setup skipped or failed ({fairPlayResult.Message}); proceeding without FairPlay.",
                    "authentication");

                var sessionUri = $"rtsp://{rtspClient.LocalIp}/{DeviceConnection.ClientSessionId}";
                var firstSetupBody = BuildInitialSetupBody(rtspClient, deviceInfo, fairPlayResult);
                var firstSetupResponse = await rtspClient.SendCustomRequestAsync(
                    "SETUP",
                    sessionUri,
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = BinaryPlistContentType,
                    },
                    firstSetupBody);

                Logger.LogMessage($"AirPlay 2 initial SETUP response:\n{firstSetupResponse.AsText()}", "authentication");
                if (firstSetupResponse.StatusCode != 200)
                {
                    return new AirPlay2MediaSetupResult
                    {
                        Success = false,
                        Message = $"AirPlay 2 initial SETUP failed with status {firstSetupResponse.StatusCode}."
                    };
                }

                var selectedCodec = ChooseAudioCodec(deviceInfo);
                var streamSetupBody = BuildAudioStreamSetupBody(selectedCodec);
                var streamSetupResponse = await rtspClient.SendCustomRequestAsync(
                    "SETUP",
                    sessionUri,
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = BinaryPlistContentType,
                        ["Transport"] = $"RTP/AVP/UDP;unicast;mode=record;control_port={LocalControlPort};timing_port={LocalTimingPort}",
                    },
                    streamSetupBody);

                Logger.LogMessage($"AirPlay 2 audio SETUP response:\n{streamSetupResponse.AsText()}", "authentication");
                if (streamSetupResponse.StatusCode != 200)
                {
                    return new AirPlay2MediaSetupResult
                    {
                        Success = false,
                        Message = $"AirPlay 2 audio SETUP failed with status {streamSetupResponse.StatusCode}."
                    };
                }

                if (!TryParseAudioPorts(streamSetupResponse.BodyBytes, out var dataPort, out var controlPort, out var timingPort))
                {
                    return new AirPlay2MediaSetupResult
                    {
                        Success = false,
                        Message = "AirPlay 2 audio SETUP succeeded but did not return data/control/timing ports."
                    };
                }

                var recordResponse = await rtspClient.SendCustomRequestAsync(
                    "RECORD",
                    sessionUri,
                    new Dictionary<string, string>
                    {
                        ["Range"] = "npt=0-",
                    });

                Logger.LogMessage($"AirPlay 2 RECORD response:\n{recordResponse.AsText()}", "authentication");
                if (recordResponse.StatusCode != 200)
                {
                    return new AirPlay2MediaSetupResult
                    {
                        Success = false,
                        Message = $"AirPlay 2 RECORD failed with status {recordResponse.StatusCode}."
                    };
                }

                return new AirPlay2MediaSetupResult
                {
                    Success = true,
                    Message = $"AirPlay 2 media session established (codec={selectedCodec}, fairPlay={fairPlayResult.Success}).",
                    DataPort = dataPort,
                    ControlPort = controlPort,
                    TimingPort = timingPort,
                    AudioLatency = ParseAudioLatency(recordResponse),
                    AudioCodec = selectedCodec,
                    // Only encrypt RTP audio when both sides agreed on FairPlay. Otherwise
                    // emit plaintext frames — receiver was told et=0.
                    AudioAesKey = fairPlayResult.HasMediaAesKey ? fairPlayResult.MediaAesKey : Array.Empty<byte>(),
                    AudioAesIv = fairPlayResult.HasMediaAesKey ? fairPlayResult.MediaAesIv : Array.Empty<byte>(),
                };
            }
            catch (Exception ex)
            {
                return new AirPlay2MediaSetupResult
                {
                    Success = false,
                    Message = $"AirPlay 2 media setup failed: {ex.Message}"
                };
            }
        }

        private static async Task SendInfoProbeAsync(RtspClient rtspClient)
        {
            try
            {
                var infoResponse = await rtspClient.SendCustomRequestAsync(
                    "GET",
                    "/info",
                    new Dictionary<string, string>
                    {
                        ["Accept"] = BinaryPlistContentType,
                    });
                Logger.LogMessage($"AirPlay 2 /info response: {infoResponse.StatusLine} bodyLen={infoResponse.BodyBytes?.Length ?? 0}", "authentication");
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"AirPlay 2 /info probe failed (non-fatal): {ex.Message}", "authentication");
            }
        }

        private static async Task<FairPlaySetupResult> RunFairPlaySetupAsync(
            RtspClient rtspClient,
            AirPlay2AuthResult authResult)
        {
            PlayFair playFair = null;
            try
            {
                playFair = new PlayFair();
                Logger.LogMessage(
                    playFair.UsesNative
                        ? "FairPlay using native sender bridge."
                        : $"FairPlay native bridge unavailable. {PlayFair.MissingDependencyMessage}",
                    "authentication");

                var m1 = PlayFair.CreateModeRequest();
                var m1Response = await rtspClient.SendCustomRequestAsync(
                    "POST",
                    "/fp-setup",
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = OctetStreamContentType,
                        ["X-Apple-ET"] = "32",
                    },
                    m1,
                    protocol: "HTTP/1.1");

                Logger.LogMessage($"AirPlay 2 fp-setup M1: {m1Response.StatusLine} bodyLen={m1Response.BodyBytes?.Length ?? 0}", "authentication");
                if (m1Response.StatusCode != 200 || (m1Response.BodyBytes?.Length ?? 0) < 12)
                {
                    return FairPlaySetupResult.Skip(
                        $"fp-setup M1 returned status {m1Response.StatusCode} bodyLen={m1Response.BodyBytes?.Length ?? 0}");
                }

                byte[] m3;
                try
                {
                    m3 = playFair.BuildSetupResponse(m1Response.BodyBytes);
                }
                catch (Exception ex)
                {
                    return FairPlaySetupResult.Skip($"FairPlay M3 build failed: {ex.Message}");
                }

                var m3Response = await rtspClient.SendCustomRequestAsync(
                    "POST",
                    "/fp-setup",
                    new Dictionary<string, string>
                    {
                        ["Content-Type"] = OctetStreamContentType,
                        ["X-Apple-ET"] = "32",
                    },
                    m3,
                    protocol: "HTTP/1.1");

                Logger.LogMessage($"AirPlay 2 fp-setup M3: {m3Response.StatusLine} bodyLen={m3Response.BodyBytes?.Length ?? 0}", "authentication");
                var rawFpResponse = PlayFair.UnwrapFply(m3Response.BodyBytes ?? Array.Empty<byte>());
                if (m3Response.StatusCode != 200 || rawFpResponse.Length == 0)
                {
                    return FairPlaySetupResult.Skip(
                        $"fp-setup M3 returned status {m3Response.StatusCode} bodyLen={m3Response.BodyBytes?.Length ?? 0}");
                }

                var mediaAesIv = RandomBytes(16);
                if (playFair.UsesManaged)
                {
                    var ekey = PlayFair.BuildManagedEkey();
                    byte[] mediaAesKey;
                    try
                    {
                        mediaAesKey = PlayFair.DeriveManagedAudioKey(m3, ekey, authResult?.SharedSecret);
                    }
                    catch (Exception ex)
                    {
                        return FairPlaySetupResult.Skip($"Managed FairPlay key derivation failed: {ex.Message}");
                    }

                    return new FairPlaySetupResult
                    {
                        Success = true,
                        MediaAesIv = mediaAesIv,
                        FairPlayEncryptedKey = ekey,
                        MediaAesKey = mediaAesKey,
                    };
                }

                var configuredMediaKey = ReadHexBytesFromEnvironment(MediaKeyHexEnvVar, PlayFair.AesKeyLength);
                var nativeMediaAesKey = configuredMediaKey ?? RandomBytes(PlayFair.AesKeyLength);

                byte[] encryptedKey;
                try
                {
                    encryptedKey = playFair.WrapAudioKey(rawFpResponse, nativeMediaAesKey);
                }
                catch (Exception ex)
                {
                    return FairPlaySetupResult.Skip($"FairPlay key wrap failed: {ex.Message}");
                }

                return new FairPlaySetupResult
                {
                    Success = true,
                    MediaAesIv = mediaAesIv,
                    FairPlayEncryptedKey = encryptedKey,
                    MediaAesKey = nativeMediaAesKey,
                };
            }
            catch (Exception ex)
            {
                return FairPlaySetupResult.Skip($"fp-setup exception: {ex.Message}");
            }
            finally
            {
                playFair?.Dispose();
            }
        }

        private static byte[] RandomBytes(int length)
        {
            var output = new byte[length];
            RandomNumberGenerator.Fill(output);
            return output;
        }

        private static Audio.AudioCodec ChooseAudioCodec(DeviceInfo deviceInfo)
        {
            // Prefer AAC-ELD for AirPlay 2 when the receiver advertises it and the native
            // encoder wrapper is present. Otherwise fall back to ALAC/L16.
            if (deviceInfo?.SupportsAacEld == true)
            {
                if (AacEldEncoder.IsAvailable())
                {
                    Logger.LogMessage("AirPlay 2 receiver advertises AAC-ELD; using AAC-ELD.", "authentication");
                    return Audio.AudioCodec.AirPlay2AacEld;
                }

                Logger.LogMessage(
                    "AirPlay 2 receiver advertises AAC-ELD, but WinStreamAacEld.dll is unavailable; falling back.",
                    "authentication");
            }

            if (deviceInfo?.SupportsAlac == true)
            {
                return Audio.AudioCodec.AppleLossless;
            }

            if (deviceInfo?.SupportsL16 == true)
            {
                return Audio.AudioCodec.L16;
            }

            return Audio.AudioCodec.L16;
        }

        private static byte[] BuildInitialSetupBody(
            RtspClient rtspClient,
            DeviceInfo deviceInfo,
            FairPlaySetupResult fairPlay)
        {
            var deviceId = NormalizeDeviceId(deviceInfo.DeviceID);
            var body = new Dictionary<string, object>
            {
                ["timingProtocol"] = "NTP",
                ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["osName"] = "Windows",
                ["osBuildVersion"] = Environment.OSVersion.Version.ToString(),
                ["sourceVersion"] = "371.4.7",
                ["timingPort"] = (ulong)LocalTimingPort,
                ["isScreenMirroringSession"] = false,
                ["osVersion"] = Environment.OSVersion.Version.ToString(),
                ["deviceID"] = deviceId,
                ["model"] = "Windows10,1",
                ["name"] = "WinStream",
                ["macAddress"] = deviceId,
            };

            // Only advertise FairPlay (et=32) when we have a real wrapped key. Without it the
            // receiver would decrypt audio against garbage and play silence/noise.
            if (fairPlay.Success &&
                fairPlay.HasMediaAesKey &&
                fairPlay.MediaAesIv?.Length == 16 &&
                fairPlay.FairPlayEncryptedKey?.Length == PlayFair.EncryptedKeyLength)
            {
                body["et"] = 32UL;
                body["eiv"] = fairPlay.MediaAesIv;
                body["ekey"] = fairPlay.FairPlayEncryptedKey;
            }
            else
            {
                body["et"] = 0UL;
            }

            return BinaryPlist.Write(body);
        }

        private static byte[] BuildAudioStreamSetupBody(Audio.AudioCodec codec)
        {
            var audioFormat = codec switch
            {
                Audio.AudioCodec.AppleLossless => AudioFormatAlac44100,
                Audio.AudioCodec.AirPlay2AacEld => AudioFormatAacEld44100,
                _ => AudioFormatPcm44100Stereo,
            };

            var stream = new Dictionary<string, object>
            {
                ["type"] = 96UL,
                ["audioFormat"] = audioFormat,
                ["streamConnectionID"] = RandomUInt64(),
            };

            var body = new Dictionary<string, object>
            {
                ["streams"] = new List<object> { stream },
            };

            return BinaryPlist.Write(body);
        }

        private static bool TryParseAudioPorts(byte[] bodyBytes, out int dataPort, out int controlPort, out int timingPort)
        {
            dataPort = 0;
            controlPort = 0;
            timingPort = 0;
            if (bodyBytes == null || bodyBytes.Length == 0)
            {
                return false;
            }

            var root = BinaryPlist.Read(bodyBytes);
            if (!BinaryPlist.TryGetUInt(root, "streams", 0, "dataPort", out var dataPortValue) ||
                !BinaryPlist.TryGetUInt(root, "streams", 0, "controlPort", out var controlPortValue) ||
                !BinaryPlist.TryGetUInt(root, "timingPort", out var timingPortValue))
            {
                return false;
            }

            dataPort = checked((int)dataPortValue);
            controlPort = checked((int)controlPortValue);
            timingPort = checked((int)timingPortValue);
            return dataPort > 0 && controlPort > 0 && timingPort > 0;
        }

        private static int ParseAudioLatency(RtspResponse response)
        {
            if (response.Headers.TryGetValue("Audio-Latency", out var value) &&
                int.TryParse(value, out var latency) &&
                latency > 0)
            {
                return latency;
            }

            return 11025;
        }

        private static string NormalizeDeviceId(string deviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                return deviceId;
            }

            var fallback = new byte[6];
            RandomNumberGenerator.Fill(fallback);
            return string.Join(":", fallback.Select(b => b.ToString("X2")));
        }

        private static ulong RandomUInt64()
        {
            Span<byte> buffer = stackalloc byte[8];
            RandomNumberGenerator.Fill(buffer);
            return BitConverter.ToUInt64(buffer);
        }

        private static byte[] ReadHexBytesFromEnvironment(string name, int expectedLength)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value
                .Replace(":", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (normalized.Length != expectedLength * 2)
            {
                Logger.LogMessage($"{name} must contain {expectedLength} hex bytes.", "authentication");
                return null;
            }

            try
            {
                return Convert.FromHexString(normalized);
            }
            catch (FormatException)
            {
                Logger.LogMessage($"{name} contains invalid hex.", "authentication");
                return null;
            }
        }

        private sealed class FairPlaySetupResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = string.Empty;
            public byte[] MediaAesIv { get; init; } = Array.Empty<byte>();
            public byte[] FairPlayEncryptedKey { get; init; } = Array.Empty<byte>();
            public byte[] MediaAesKey { get; init; } = Array.Empty<byte>();
            public bool HasMediaAesKey => MediaAesKey?.Length == 16;

            // Skipped means fp-setup was not supported/needed — caller continues without FairPlay.
            public static FairPlaySetupResult Skip(string reason)
            {
                return new FairPlaySetupResult
                {
                    Success = false,
                    Message = reason,
                };
            }
        }
    }
}
