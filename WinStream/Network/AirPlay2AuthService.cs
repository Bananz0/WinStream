using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace WinStream.Network
{
    public sealed class PairVerifyProbeResult
    {
        public bool Success { get; init; }
        public string Message { get; init; }
        public string StatusLine { get; init; }
    }

    public sealed class AirPlay2AuthResult
    {
        public bool Success { get; init; }
        public bool IsTransient { get; init; }
        public bool PinRequired { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    internal sealed class StoredAirPlayCredentials
    {
        public string AccessoryIdentifier { get; set; } = string.Empty;
        public string AccessoryPublicKeyBase64 { get; set; } = string.Empty;
        public string ClientIdentifier { get; set; } = string.Empty;
        public string ClientPrivateKeyBase64 { get; set; } = string.Empty;
        public string ClientPublicKeyBase64 { get; set; } = string.Empty;
    }

    public static class AirPlay2AuthService
    {
        public const string PinEnvironmentVariable = "WINSTREAM_AIRPLAY_PIN";
        private const string PairSetupIdentity = "Pair-Setup";
        private const string TransientPin = "3939";
        private const string PinEnvVar = PinEnvironmentVariable;
        private const string PairingStoreFileName = "airplay2_pairings.json";
        private static readonly string[] HapPostProtocols = { "HTTP/1.1", "RTSP/1.0" };
        private static readonly int[] PairingHkpVersions = { 3, 4 };
        private static string _runtimePin = string.Empty;

        private static readonly byte[] AuthSetupCurve25519PubKey =
        {
            0x59, 0x02, 0xED, 0xE9, 0x0D, 0x4E, 0xF2, 0xBD,
            0x4C, 0xB6, 0x8A, 0x63, 0x30, 0x03, 0x82, 0x07,
            0xA9, 0x4D, 0xBD, 0x50, 0xD8, 0xAA, 0x46, 0x5B,
            0x5D, 0x8C, 0x01, 0x2A, 0x0C, 0x7E, 0x1D, 0x4E
        };

        public static async Task<AirPlay2AuthResult> AuthenticateAndEnableEncryptionAsync(
            RtspClient rtspClient,
            DeviceInfo deviceInfo)
        {
            try
            {
                var authSetupResponse = await TryAuthSetupAsync(rtspClient);
                if (IsForbiddenOrUnauthorized(authSetupResponse.StatusCode))
                {
                    var rejectedMessage = BuildPairingRejectedMessage(authSetupResponse.StatusCode);
                    Logger.LogMessage(rejectedMessage, "authentication");
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = false,
                        Message = rejectedMessage
                    };
                }

                var stored = LoadStoredCredentials(deviceInfo);
                if (stored != null)
                {
                    var pairVerifyResult = await TryPairVerifyWithStoredCredentialsAsync(rtspClient, stored);
                    if (pairVerifyResult.Success)
                    {
                        Logger.LogMessage("AirPlay 2 pair-verify succeeded with stored credentials.", "authentication");
                        return pairVerifyResult;
                    }

                    Logger.LogMessage(
                        $"Stored AirPlay 2 credentials failed pair-verify: {pairVerifyResult.Message}",
                        "authentication");
                }

                var pin = ResolveConfiguredPin();
                if (!string.IsNullOrWhiteSpace(pin))
                {
                    var runtimePinActive = !string.IsNullOrWhiteSpace(_runtimePin);
                    Logger.LogMessage(
                        $"Using configured AirPlay PIN (runtimePinActive={runtimePinActive}).",
                        "authentication");
                    var pairSetupResult = await TryFullPairSetupAsync(
                        rtspClient,
                        pin.Trim(),
                        initiatePinStart: !runtimePinActive);
                    if (pairSetupResult.Success && pairSetupResult.Credentials != null)
                    {
                        SaveStoredCredentials(deviceInfo, pairSetupResult.Credentials);
                        return await TryPairVerifyWithStoredCredentialsAsync(rtspClient, pairSetupResult.Credentials);
                    }

                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = ShouldPromptForPinRetry(pairSetupResult.Message),
                        Message = pairSetupResult.Message
                    };
                }

                var pinAvailability = await TryPinPairingAvailabilityAsync(rtspClient);
                if (pinAvailability.PinRequired)
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = true,
                        Message =
                            $"{pinAvailability.Message} Provide PIN in the app or set {PinEnvVar} to perform full pair-setup and persist credentials."
                    };
                }

                var transientResult = await TryTransientPairingAsync(rtspClient);
                if (transientResult.Success)
                {
                    return transientResult;
                }

                return transientResult;
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"AirPlay 2 authentication failed: {ex.GetType().Name}: {ex.Message}", "authentication");
                return new AirPlay2AuthResult
                {
                    Success = false,
                    Message = $"AirPlay 2 authentication failed: {ex.Message}"
                };
            }
        }

        private static bool ShouldPromptForPinRetry(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            if (message.Contains("server proof verification failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("HAP error 2", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (message.Contains(" 401", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("(401", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("status 401", StringComparison.OrdinalIgnoreCase) ||
                message.Contains(" 403", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("(403", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("status 403", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("requires Apple-account/current-user authorization", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return false;
        }

        public static async Task<PairVerifyProbeResult> TryPairVerifyProbeAsync(string ipAddress, int port)
        {
            try
            {
                var secureRandom = new SecureRandom();
                var privateKey = new X25519PrivateKeyParameters(secureRandom);
                var publicKey = privateKey.GeneratePublicKey().GetEncoded();
                var body = HapTlv8.Encode(
                    ((byte)HapTlvType.State, new byte[] { 0x01 }),
                    ((byte)HapTlvType.PublicKey, publicKey));
                PairVerifyProbeResult lastResult = null;

                foreach (var protocol in HapPostProtocols)
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(ipAddress, port).WaitAsync(TimeSpan.FromSeconds(3));
                    using var stream = client.GetStream();

                    var request = BuildSimpleRequest(
                        "POST",
                        "/pair-verify",
                        protocol,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["CSeq"] = "1",
                            ["User-Agent"] = "AirPlay/550.10",
                            ["Connection"] = "keep-alive",
                            ["X-Apple-HKP"] = "3",
                            ["Content-Type"] = "application/octet-stream"
                        },
                        body);

                    await stream.WriteAsync(request, 0, request.Length).WaitAsync(TimeSpan.FromSeconds(3));
                    await stream.FlushAsync().WaitAsync(TimeSpan.FromSeconds(3));

                    var response = await ReadSimpleResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(4));
                    if (response.StatusCode == 200)
                    {
                        var state = ParseTlvState(response.BodyBytes);
                        var stateText = state.HasValue ? state.Value.ToString(CultureInfo.InvariantCulture) : "unknown";
                        return new PairVerifyProbeResult
                        {
                            Success = true,
                            Message = $"pair-verify probe accepted (state={stateText})",
                            StatusLine = response.StatusLine
                        };
                    }

                    lastResult = new PairVerifyProbeResult
                    {
                        Success = false,
                        Message = $"pair-verify probe returned: {response.StatusLine}",
                        StatusLine = response.StatusLine
                    };

                    if (!ShouldRetryAlternateProtocol(response))
                    {
                        break;
                    }
                }

                return lastResult ?? new PairVerifyProbeResult
                {
                    Success = false,
                    Message = "pair-verify probe did not return a usable response.",
                    StatusLine = string.Empty
                };
            }
            catch (Exception ex)
            {
                return new PairVerifyProbeResult
                {
                    Success = false,
                    Message = ex.Message,
                    StatusLine = string.Empty
                };
            }
        }

        public static void SetRuntimePin(string pin)
        {
            _runtimePin = pin?.Trim() ?? string.Empty;
        }

        public static void ClearRuntimePin()
        {
            _runtimePin = string.Empty;
        }

        private static string ResolveConfiguredPin()
        {
            if (!string.IsNullOrWhiteSpace(_runtimePin))
            {
                return _runtimePin;
            }

            return Environment.GetEnvironmentVariable(PinEnvVar)?.Trim() ?? string.Empty;
        }

        private static async Task<(bool PinRequired, string Message)> TryPinPairingAvailabilityAsync(RtspClient rtspClient)
        {
            RtspResponse last = null;
            foreach (var hkpVersion in PairingHkpVersions)
            {
                var response = await SendPairPinStartAsync(rtspClient, hkpVersion: hkpVersion);
                last = response;
                if (response.StatusCode == 200)
                {
                    return (true, $"Receiver is waiting for PIN pairing (HKP {hkpVersion}).");
                }

                if (IsForbiddenOrUnauthorized(response.StatusCode))
                {
                    return (false, BuildPairingRejectedMessage(response.StatusCode));
                }
            }

            return (false, $"PIN pairing not available: {last?.StatusLine ?? "no response"}");
        }

        private static async Task<AirPlay2AuthResult> TryTransientPairingAsync(RtspClient rtspClient)
        {
            try
            {
                var pairPinStartResponse = await SendPairPinStartAsync(rtspClient, hkpVersion: 4);
                var pinFlowAvailable = pairPinStartResponse.StatusCode == 200;

                var m2 = await SendPairSetupM1Async(rtspClient, pinMethod: 0x00, hkpVersion: 4, transient: true);
                if (!m2.Success)
                {
                    if (IsForbiddenOrUnauthorized(m2.StatusCode))
                    {
                        return new AirPlay2AuthResult
                        {
                            Success = false,
                            PinRequired = false,
                            Message = BuildPairingRejectedMessage(m2.StatusCode)
                        };
                    }

                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = pinFlowAvailable,
                        Message = pinFlowAvailable
                            ? $"Transient pair-setup failed: {m2.Message}"
                            : m2.Message
                    };
                }

                var srp = CreateSrpClient();
                var m3Payload = srp.CreateStepM3Payload(m2.Salt, m2.ServerPublicKey, TransientPin);
                var m4Response = await PostTlvAsync(rtspClient, "/pair-setup", m3Payload, hkpVersion: 4);
                if (m4Response.StatusCode != 200)
                {
                    if (IsForbiddenOrUnauthorized(m4Response.StatusCode))
                    {
                        return new AirPlay2AuthResult
                        {
                            Success = false,
                            PinRequired = false,
                            Message = BuildPairingRejectedMessage(m4Response.StatusCode)
                        };
                    }

                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = pinFlowAvailable,
                        Message = $"Transient pair-setup M3 failed with status {m4Response.StatusCode}."
                    };
                }

                var m4Tlv = HapTlv8.Decode(m4Response.BodyBytes);
                if (m4Tlv.TryGetValue((byte)HapTlvType.Error, out var transientError) && transientError.Length > 0)
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = pinFlowAvailable || transientError[0] == 0x02,
                        Message = $"Transient pair-setup rejected with HAP error {transientError[0]}."
                    };
                }

                if (!m4Tlv.TryGetValue((byte)HapTlvType.Proof, out var serverProof))
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = pinFlowAvailable,
                        Message = "Transient pair-setup did not return server proof."
                    };
                }

                if (!srp.VerifyServerProof(serverProof))
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        PinRequired = pinFlowAvailable,
                        Message = "Transient pair-setup server proof verification failed."
                    };
                }

                var sessionKey = srp.GetSessionKey();
                EnableControlEncryption(rtspClient, sessionKey);
                Logger.LogMessage("AirPlay 2 transient pairing succeeded.", "authentication");
                return new AirPlay2AuthResult
                {
                    Success = true,
                    IsTransient = true,
                    Message = "AirPlay 2 transient pairing succeeded."
                };
            }
            catch (Exception ex)
            {
                return new AirPlay2AuthResult
                {
                    Success = false,
                    PinRequired = false,
                    Message = $"Transient pair-setup failed: {ex.Message}"
                };
            }
        }

        private static async Task<(bool Success, string Message, StoredAirPlayCredentials Credentials)> TryFullPairSetupAsync(
            RtspClient rtspClient,
            string pin,
            bool initiatePinStart)
        {
            var attemptMessages = new List<string>(PairingHkpVersions.Length);
            foreach (var hkpVersion in PairingHkpVersions)
            {
                var attempt = await TryFullPairSetupWithVersionAsync(rtspClient, pin, hkpVersion, initiatePinStart);
                if (attempt.Success)
                {
                    return attempt;
                }

                attemptMessages.Add($"HKP {hkpVersion}: {attempt.Message}");
                if (!ShouldRetryWithAlternateHkp(attempt.Message))
                {
                    return attempt;
                }
            }

            return (false, $"Pair-setup failed after HKP retries. {string.Join(" ", attemptMessages)}", null);
        }

        private static async Task<(bool Success, string Message, StoredAirPlayCredentials Credentials)> TryFullPairSetupWithVersionAsync(
            RtspClient rtspClient,
            string pin,
            int hkpVersion,
            bool initiatePinStart)
        {
            try
            {
                using var pairSetupClient = new RtspClient(rtspClient.ServerIp, rtspClient.ServerPort, rsaPublicKey: null);
                Logger.LogMessage(
                    $"Created side-channel RTSP client for pair-setup HKP {hkpVersion}.",
                    "authentication");

                Logger.LogMessage(
                    $"Starting pair-setup with HKP {hkpVersion} (initiatePinStart={initiatePinStart}).",
                    "authentication");

                var authSetup = await TryAuthSetupAsync(pairSetupClient);
                if (IsForbiddenOrUnauthorized(authSetup.StatusCode))
                {
                    return (false, BuildPairingRejectedMessage(authSetup.StatusCode), null);
                }

                if (initiatePinStart)
                {
                    var pinStart = await SendPairPinStartAsync(pairSetupClient, hkpVersion);
                    if (IsForbiddenOrUnauthorized(pinStart.StatusCode))
                    {
                        return (false, BuildPairingRejectedMessage(pinStart.StatusCode), null);
                    }
                }

                var m2 = await SendPairSetupM1Async(pairSetupClient, pinMethod: 0x00, hkpVersion: hkpVersion, transient: false);
                if (!m2.Success)
                {
                    return (false, m2.Message, null);
                }

                var srp = CreateSrpClient();
                var m3Payload = srp.CreateStepM3Payload(m2.Salt, m2.ServerPublicKey, pin);
                var m4Response = await PostTlvAsync(pairSetupClient, "/pair-setup", m3Payload, hkpVersion);
                if (m4Response.StatusCode != 200)
                {
                    return (false, $"Pair-setup M3 failed: {m4Response.StatusLine}.", null);
                }

                var m4Tlv = HapTlv8.Decode(m4Response.BodyBytes);
                if (!m4Tlv.TryGetValue((byte)HapTlvType.Proof, out var serverProof))
                {
                    return (false, "Pair-setup M4 missing server proof.", null);
                }

                if (!srp.VerifyServerProof(serverProof))
                {
                    return (false, "Pair-setup M4 server proof verification failed.", null);
                }

                var sessionKey = srp.GetSessionKey();
                var pairSetupEncryptKey = HkdfSha512(
                    sessionKey,
                    "Pair-Setup-Encrypt-Salt",
                    "Pair-Setup-Encrypt-Info",
                    32);
                var controllerSignKey = HkdfSha512(
                    sessionKey,
                    "Pair-Setup-Controller-Sign-Salt",
                    "Pair-Setup-Controller-Sign-Info",
                    32);

                var random = new SecureRandom();
                var clientLtsk = new Ed25519PrivateKeyParameters(random);
                var clientLtpk = clientLtsk.GeneratePublicKey();
                var clientIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
                var clientIdBytes = Encoding.UTF8.GetBytes(clientIdentifier);

                var toSign = Concat(controllerSignKey, clientIdBytes, clientLtpk.GetEncoded());
                var clientSignature = SignEd25519(clientLtsk, toSign);
                var innerTlv = HapTlv8.Encode(
                    ((byte)HapTlvType.Identifier, clientIdBytes),
                    ((byte)HapTlvType.PublicKey, clientLtpk.GetEncoded()),
                    ((byte)HapTlvType.Signature, clientSignature));
                var encryptedM5 = EncryptChaCha(pairSetupEncryptKey, Encoding.ASCII.GetBytes("PS-Msg05"), innerTlv, null);
                var m5Payload = HapTlv8.Encode(
                    ((byte)HapTlvType.State, new byte[] { 0x05 }),
                    ((byte)HapTlvType.EncryptedData, encryptedM5));

                var m6Response = await PostTlvAsync(pairSetupClient, "/pair-setup", m5Payload, hkpVersion);
                if (m6Response.StatusCode != 200)
                {
                    return (false, $"Pair-setup M5 failed: {m6Response.StatusLine}.", null);
                }

                var m6Tlv = HapTlv8.Decode(m6Response.BodyBytes);
                if (!m6Tlv.TryGetValue((byte)HapTlvType.EncryptedData, out var encryptedM6))
                {
                    return (false, "Pair-setup M6 missing encrypted data.", null);
                }

                var decryptedM6 = DecryptChaCha(pairSetupEncryptKey, Encoding.ASCII.GetBytes("PS-Msg06"), encryptedM6, null);
                var m6Inner = HapTlv8.Decode(decryptedM6);
                if (!m6Inner.TryGetValue((byte)HapTlvType.Identifier, out var accessoryIdentifier) ||
                    !m6Inner.TryGetValue((byte)HapTlvType.PublicKey, out var accessoryLtpkBytes) ||
                    !m6Inner.TryGetValue((byte)HapTlvType.Signature, out var accessorySignature))
                {
                    return (false, "Pair-setup M6 missing required accessory credentials.", null);
                }

                var accessorySignKey = HkdfSha512(
                    sessionKey,
                    "Pair-Setup-Accessory-Sign-Salt",
                    "Pair-Setup-Accessory-Sign-Info",
                    32);
                var accessorySignedData = Concat(accessorySignKey, accessoryIdentifier, accessoryLtpkBytes);
                var accessoryLtpk = new Ed25519PublicKeyParameters(accessoryLtpkBytes);
                if (!VerifyEd25519(accessoryLtpk, accessorySignedData, accessorySignature))
                {
                    return (false, "Pair-setup M6 accessory signature validation failed.", null);
                }

                var credentials = new StoredAirPlayCredentials
                {
                    AccessoryIdentifier = Encoding.UTF8.GetString(accessoryIdentifier),
                    AccessoryPublicKeyBase64 = Convert.ToBase64String(accessoryLtpkBytes),
                    ClientIdentifier = clientIdentifier,
                    ClientPrivateKeyBase64 = Convert.ToBase64String(clientLtsk.GetEncoded()),
                    ClientPublicKeyBase64 = Convert.ToBase64String(clientLtpk.GetEncoded()),
                };

                return (true, $"Pair-setup succeeded with HKP {hkpVersion}.", credentials);
            }
            catch (Exception ex)
            {
                return (false, $"Pair-setup failed (HKP {hkpVersion}): {ex.Message}", null);
            }
        }

        private static async Task<AirPlay2AuthResult> TryPairVerifyWithStoredCredentialsAsync(
            RtspClient rtspClient,
            StoredAirPlayCredentials credentials)
        {
            AirPlay2AuthResult lastResult = null;
            foreach (var hkpVersion in PairingHkpVersions)
            {
                var attempt = await TryPairVerifyWithStoredCredentialsForVersionAsync(rtspClient, credentials, hkpVersion);
                if (attempt.Success)
                {
                    return attempt;
                }

                lastResult = attempt;
                if (!ShouldRetryWithAlternateHkp(attempt.Message))
                {
                    return attempt;
                }
            }

            return lastResult ?? new AirPlay2AuthResult
            {
                Success = false,
                Message = "Pair-verify failed: no usable response."
            };
        }

        private static async Task<AirPlay2AuthResult> TryPairVerifyWithStoredCredentialsForVersionAsync(
            RtspClient rtspClient,
            StoredAirPlayCredentials credentials,
            int hkpVersion)
        {
            try
            {
                var random = new SecureRandom();
                var verifyPrivate = new X25519PrivateKeyParameters(random);
                var verifyPublic = verifyPrivate.GeneratePublicKey().GetEncoded();

                var m1Payload = HapTlv8.Encode(
                    ((byte)HapTlvType.State, new byte[] { 0x01 }),
                    ((byte)HapTlvType.PublicKey, verifyPublic));
                var m2Response = await PostTlvAsync(rtspClient, "/pair-verify", m1Payload, hkpVersion: hkpVersion);
                if (m2Response.StatusCode != 200)
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        Message = $"Pair-verify M1 failed with status {m2Response.StatusCode} (HKP {hkpVersion})."
                    };
                }

                var m2Tlv = HapTlv8.Decode(m2Response.BodyBytes);
                if (!m2Tlv.TryGetValue((byte)HapTlvType.PublicKey, out var sessionPublicKey) ||
                    !m2Tlv.TryGetValue((byte)HapTlvType.EncryptedData, out var encryptedM2))
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        Message = $"Pair-verify M2 missing required fields (HKP {hkpVersion})."
                    };
                }

                var sharedSecret = new byte[32];
                verifyPrivate.GenerateSecret(new X25519PublicKeyParameters(sessionPublicKey), sharedSecret, 0);
                var verifyEncryptKey = HkdfSha512(
                    sharedSecret,
                    "Pair-Verify-Encrypt-Salt",
                    "Pair-Verify-Encrypt-Info",
                    32);
                var decryptedM2 = DecryptChaCha(verifyEncryptKey, Encoding.ASCII.GetBytes("PV-Msg02"), encryptedM2, null);
                var m2Inner = HapTlv8.Decode(decryptedM2);
                if (!m2Inner.TryGetValue((byte)HapTlvType.Identifier, out var accessoryIdentifier) ||
                    !m2Inner.TryGetValue((byte)HapTlvType.Signature, out var accessorySignature))
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        Message = $"Pair-verify M2 decrypted payload missing identifier/signature (HKP {hkpVersion})."
                    };
                }

                if (!string.Equals(
                        Encoding.UTF8.GetString(accessoryIdentifier),
                        credentials.AccessoryIdentifier,
                        StringComparison.Ordinal))
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        Message = "Pair-verify M2 accessory identifier mismatch with stored credentials."
                    };
                }

                var accessoryLtpk = new Ed25519PublicKeyParameters(Convert.FromBase64String(credentials.AccessoryPublicKeyBase64));
                var accessorySignedData = Concat(sessionPublicKey, accessoryIdentifier, verifyPublic);
                if (!VerifyEd25519(accessoryLtpk, accessorySignedData, accessorySignature))
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        Message = "Pair-verify M2 accessory signature validation failed."
                    };
                }

                var clientLtsk = new Ed25519PrivateKeyParameters(Convert.FromBase64String(credentials.ClientPrivateKeyBase64));
                var clientIdentifier = Encoding.UTF8.GetBytes(credentials.ClientIdentifier);
                var clientSignedData = Concat(verifyPublic, clientIdentifier, sessionPublicKey);
                var clientSignature = SignEd25519(clientLtsk, clientSignedData);
                var m3Inner = HapTlv8.Encode(
                    ((byte)HapTlvType.Identifier, clientIdentifier),
                    ((byte)HapTlvType.Signature, clientSignature));
                var encryptedM3 = EncryptChaCha(verifyEncryptKey, Encoding.ASCII.GetBytes("PV-Msg03"), m3Inner, null);
                var m3Payload = HapTlv8.Encode(
                    ((byte)HapTlvType.State, new byte[] { 0x03 }),
                    ((byte)HapTlvType.EncryptedData, encryptedM3));
                var m4Response = await PostTlvAsync(rtspClient, "/pair-verify", m3Payload, hkpVersion: hkpVersion);
                if (m4Response.StatusCode != 200)
                {
                    return new AirPlay2AuthResult
                    {
                        Success = false,
                        Message = $"Pair-verify M3 failed with status {m4Response.StatusCode} (HKP {hkpVersion})."
                    };
                }

                EnableControlEncryption(rtspClient, sharedSecret);
                Logger.LogMessage($"AirPlay 2 pair-verify succeeded and control encryption enabled (HKP {hkpVersion}).", "authentication");
                return new AirPlay2AuthResult
                {
                    Success = true,
                    Message = $"AirPlay 2 pair-verify succeeded and control encryption enabled (HKP {hkpVersion})."
                };
            }
            catch (Exception ex)
            {
                return new AirPlay2AuthResult
                {
                    Success = false,
                    Message = $"Pair-verify failed (HKP {hkpVersion}): {ex.Message}"
                };
            }
        }

        private static void EnableControlEncryption(RtspClient rtspClient, byte[] encryptionKeyBase)
        {
            var writeKey = HkdfSha512(
                encryptionKeyBase,
                "Control-Salt",
                "Control-Write-Encryption-Key",
                32);
            var readKey = HkdfSha512(
                encryptionKeyBase,
                "Control-Salt",
                "Control-Read-Encryption-Key",
                32);
            rtspClient.EnableHapEncryption(writeKey, readKey);
        }

        private static async Task<RtspResponse> SendPairPinStartAsync(RtspClient rtspClient, int hkpVersion)
        {
            var response = await SendPostWithProtocolFallbackAsync(
                rtspClient,
                "/pair-pin-start",
                BuildHapHeaders(hkpVersion),
                Array.Empty<byte>());

            // Some receivers do not expose /pair-pin-start. Ignore non-200 and continue.
            Logger.LogMessage(
                $"/pair-pin-start response: {response.StatusLine}",
                "authentication");
            return response;
        }

        private static async Task<(bool Success, string Message, int StatusCode, byte[] Salt, byte[] ServerPublicKey)> SendPairSetupM1Async(
            RtspClient rtspClient,
            byte pinMethod,
            int hkpVersion,
            bool transient)
        {
            var payload = transient
                ? HapTlv8.Encode(
                    ((byte)HapTlvType.Method, new[] { pinMethod }),
                    ((byte)HapTlvType.State, new byte[] { 0x01 }),
                    ((byte)HapTlvType.Flags, new byte[] { 0x10 }))
                : HapTlv8.Encode(
                    ((byte)HapTlvType.Method, new[] { pinMethod }),
                    ((byte)HapTlvType.State, new byte[] { 0x01 }));

            var response = await PostTlvAsync(rtspClient, "/pair-setup", payload, hkpVersion);
            if (response.StatusCode != 200)
            {
                return (false, $"Pair-setup M1 failed: {response.StatusLine}.", response.StatusCode, null, null);
            }

            var decoded = HapTlv8.Decode(response.BodyBytes);
            if (decoded.TryGetValue((byte)HapTlvType.Error, out var errorCode) && errorCode.Length > 0)
            {
                return (false, $"Pair-setup M2 returned HAP error {errorCode[0]}.", response.StatusCode, null, null);
            }

            if (!decoded.TryGetValue((byte)HapTlvType.Salt, out var salt) ||
                !decoded.TryGetValue((byte)HapTlvType.PublicKey, out var serverPublic))
            {
                return (false, "Pair-setup M2 missing salt/public key.", response.StatusCode, null, null);
            }

            return (true, string.Empty, response.StatusCode, salt, serverPublic);
        }

        private static async Task<RtspResponse> PostTlvAsync(
            RtspClient rtspClient,
            string path,
            byte[] body,
            int hkpVersion)
        {
            return await SendPostWithProtocolFallbackAsync(rtspClient, path, BuildHapHeaders(hkpVersion), body);
        }

        private static async Task<RtspResponse> TryAuthSetupAsync(RtspClient rtspClient)
        {
            var body = new byte[33];
            body[0] = 0x01;
            Buffer.BlockCopy(AuthSetupCurve25519PubKey, 0, body, 1, AuthSetupCurve25519PubKey.Length);
            var response = await SendPostWithProtocolFallbackAsync(
                rtspClient,
                "/auth-setup",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Content-Type"] = "application/octet-stream",
                    ["Connection"] = "keep-alive",
                    ["User-Agent"] = "AirPlay/550.10",
                },
                body);

            Logger.LogMessage($"/auth-setup response: {response.StatusLine}", "authentication");
            return response;
        }

        private static Dictionary<string, string> BuildHapHeaders(int hkpVersion)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["User-Agent"] = "AirPlay/550.10",
                ["Connection"] = "keep-alive",
                ["X-Apple-HKP"] = hkpVersion.ToString(CultureInfo.InvariantCulture),
                ["Content-Type"] = "application/octet-stream",
            };
        }

        private static async Task<RtspResponse> SendPostWithProtocolFallbackAsync(
            RtspClient rtspClient,
            string path,
            Dictionary<string, string> headers,
            byte[] body)
        {
            RtspResponse last = null;

            foreach (var protocol in HapPostProtocols)
            {
                var requestHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
                var response = await rtspClient.SendCustomRequestAsync(
                    "POST",
                    path,
                    requestHeaders,
                    body,
                    protocol: protocol);
                last = response;

                if (!ShouldRetryAlternateProtocol(response))
                {
                    return response;
                }

                Logger.LogMessage(
                    $"{path} returned {response.StatusLine} over {protocol}. Retrying with alternate protocol.",
                    "authentication");
            }

            return last ?? new RtspResponse
            {
                StatusLine = "RTSP_ERROR No response",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                BodyBytes = Array.Empty<byte>()
            };
        }

        private static bool ShouldRetryAlternateProtocol(RtspResponse response)
        {
            if (response == null || string.IsNullOrWhiteSpace(response.StatusLine))
            {
                return true;
            }

            if (response.StatusLine.StartsWith("RTSP_ERROR", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Retry with alternate request framing only for likely protocol/framing failures.
            return response.StatusCode == 0 ||
                   response.StatusCode == 400 ||
                   response.StatusCode == 404 ||
                   response.StatusCode == 405 ||
                   response.StatusCode == 500 ||
                   response.StatusCode == 501;
        }

        private static bool ShouldRetryWithAlternateHkp(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return !message.Contains(" 401", StringComparison.OrdinalIgnoreCase) &&
                   !message.Contains("(401", StringComparison.OrdinalIgnoreCase) &&
                   !message.Contains("status 401", StringComparison.OrdinalIgnoreCase) &&
                   !message.Contains(" 403", StringComparison.OrdinalIgnoreCase) &&
                   !message.Contains("(403", StringComparison.OrdinalIgnoreCase) &&
                   !message.Contains("status 403", StringComparison.OrdinalIgnoreCase) &&
                   !message.Contains("HAP error 2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsForbiddenOrUnauthorized(int statusCode)
        {
            return statusCode == 401 || statusCode == 403;
        }

        private static string BuildPairingRejectedMessage(int statusCode)
        {
            return $"Receiver rejected pairing setup ({statusCode}). This receiver likely requires Apple-account/current-user authorization and may not support PIN pairing from WinStream.";
        }

        private static SrpClientSession CreateSrpClient()
        {
            return new SrpClientSession();
        }

        private static byte[] HkdfSha512(byte[] ikm, string salt, string info, int outputLength)
        {
            var generator = new HkdfBytesGenerator(new Sha512Digest());
            generator.Init(new HkdfParameters(ikm, Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info)));
            var output = new byte[outputLength];
            generator.GenerateBytes(output, 0, outputLength);
            return output;
        }

        private static byte[] EncryptChaCha(byte[] key, byte[] nonceInput, byte[] plaintext, byte[] aad)
        {
            return ChaChaCryptInternal(key, nonceInput, plaintext, aad, encrypt: true);
        }

        private static byte[] DecryptChaCha(byte[] key, byte[] nonceInput, byte[] ciphertextAndTag, byte[] aad)
        {
            return ChaChaCryptInternal(key, nonceInput, ciphertextAndTag, aad, encrypt: false);
        }

        private static byte[] ChaChaCryptInternal(
            byte[] key,
            byte[] nonceInput,
            byte[] input,
            byte[] aad,
            bool encrypt)
        {
            var nonce = NormalizeNonce(nonceInput);
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(
                encrypt,
                new AeadParameters(new KeyParameter(key), 128, nonce, aad));

            var output = new byte[cipher.GetOutputSize(input.Length)];
            var len = cipher.ProcessBytes(input, 0, input.Length, output, 0);
            len += cipher.DoFinal(output, len);
            if (len == output.Length)
            {
                return output;
            }

            var trimmed = new byte[len];
            Buffer.BlockCopy(output, 0, trimmed, 0, len);
            return trimmed;
        }

        private static byte[] NormalizeNonce(byte[] nonceInput)
        {
            if (nonceInput == null)
            {
                throw new ArgumentNullException(nameof(nonceInput));
            }

            if (nonceInput.Length == 12)
            {
                return nonceInput;
            }

            if (nonceInput.Length == 8)
            {
                var nonce = new byte[12];
                Buffer.BlockCopy(nonceInput, 0, nonce, 4, 8);
                return nonce;
            }

            throw new ArgumentException("Nonce must be 8 or 12 bytes for AirPlay HAP ChaCha20-Poly1305.");
        }

        private static byte[] SignEd25519(Ed25519PrivateKeyParameters privateKey, byte[] message)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, privateKey);
            signer.BlockUpdate(message, 0, message.Length);
            return signer.GenerateSignature();
        }

        private static bool VerifyEd25519(Ed25519PublicKeyParameters publicKey, byte[] message, byte[] signature)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, publicKey);
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }

        private static byte[] Concat(params byte[][] segments)
        {
            var total = 0;
            foreach (var segment in segments)
            {
                total += segment?.Length ?? 0;
            }

            var output = new byte[total];
            var offset = 0;
            foreach (var segment in segments)
            {
                if (segment == null || segment.Length == 0)
                {
                    continue;
                }

                Buffer.BlockCopy(segment, 0, output, offset, segment.Length);
                offset += segment.Length;
            }

            return output;
        }

        private static byte? ParseTlvState(byte[] body)
        {
            if (body == null || body.Length < 3)
            {
                return null;
            }

            var decoded = HapTlv8.Decode(body);
            return decoded.TryGetValue((byte)HapTlvType.State, out var state) && state.Length >= 1 ? state[0] : null;
        }

        private static string PairingStorePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinStream",
                "Pairings",
                PairingStoreFileName);

        private static string ResolveDeviceKey(DeviceInfo deviceInfo)
        {
            if (!string.IsNullOrWhiteSpace(deviceInfo.DeviceID))
            {
                return deviceInfo.DeviceID.Trim();
            }

            return $"{deviceInfo.DisplayName}|{deviceInfo.IPAddress}|{deviceInfo.Port}";
        }

        private static StoredAirPlayCredentials LoadStoredCredentials(DeviceInfo deviceInfo)
        {
            try
            {
                var path = PairingStorePath;
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                var map = JsonSerializer.Deserialize<Dictionary<string, StoredAirPlayCredentials>>(json);
                if (map == null)
                {
                    return null;
                }

                map.TryGetValue(ResolveDeviceKey(deviceInfo), out var value);
                return value;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveStoredCredentials(DeviceInfo deviceInfo, StoredAirPlayCredentials credentials)
        {
            try
            {
                var path = PairingStorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var map = new Dictionary<string, StoredAirPlayCredentials>();
                if (File.Exists(path))
                {
                    var existingJson = File.ReadAllText(path);
                    var existingMap = JsonSerializer.Deserialize<Dictionary<string, StoredAirPlayCredentials>>(existingJson);
                    if (existingMap != null)
                    {
                        map = existingMap;
                    }
                }

                map[ResolveDeviceKey(deviceInfo)] = credentials;
                var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                Logger.LogMessage($"Saved AirPlay 2 credentials to {path}", "authentication");
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Failed to persist AirPlay 2 credentials: {ex.Message}", "authentication");
            }
        }

        private static byte[] BuildSimpleRequest(
            string method,
            string path,
            string protocol,
            Dictionary<string, string> headers,
            byte[] body)
        {
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(path).Append(' ').Append(protocol).Append("\r\n");
            foreach (var header in headers)
            {
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            if (body == null || body.Length == 0)
            {
                return head;
            }

            var combined = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, combined, 0, head.Length);
            Buffer.BlockCopy(body, 0, combined, head.Length, body.Length);
            return combined;
        }

        private static async Task<RtspResponse> ReadSimpleResponseAsync(NetworkStream stream)
        {
            var buffer = new List<byte>(4096);
            var temp = new byte[4096];
            while (true)
            {
                if (TryExtractSimpleResponse(buffer, out var response))
                {
                    return response;
                }

                var read = await stream.ReadAsync(temp, 0, temp.Length);
                if (read <= 0)
                {
                    throw new IOException("Connection closed while waiting for response.");
                }

                for (var i = 0; i < read; i++)
                {
                    buffer.Add(temp[i]);
                }
            }
        }

        private static bool TryExtractSimpleResponse(List<byte> buffer, out RtspResponse response)
        {
            response = null;
            var headerTerminator = IndexOfCrlfCrlf(buffer);
            if (headerTerminator < 0)
            {
                return false;
            }

            var headerBytes = buffer.GetRange(0, headerTerminator).ToArray();
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
            {
                return false;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var idx = line.IndexOf(':');
                if (idx > 0)
                {
                    headers[line.Substring(0, idx).Trim()] = line.Substring(idx + 1).Trim();
                }
            }

            var contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var lenText))
            {
                _ = int.TryParse(lenText, out contentLength);
            }

            var bodyOffset = headerTerminator + 4;
            if (buffer.Count < bodyOffset + contentLength)
            {
                return false;
            }

            var body = contentLength > 0 ? buffer.GetRange(bodyOffset, contentLength).ToArray() : Array.Empty<byte>();
            buffer.RemoveRange(0, bodyOffset + contentLength);
            response = new RtspResponse
            {
                StatusLine = lines[0],
                Headers = headers,
                BodyBytes = body
            };
            return true;
        }

        private static int IndexOfCrlfCrlf(List<byte> buffer)
        {
            for (var i = 0; i <= buffer.Count - 4; i++)
            {
                if (buffer[i] == '\r' &&
                    buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' &&
                    buffer[i + 3] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        private sealed class SrpClientSession
        {
            private readonly Srp6Client _srpClient = new();
            private byte[] _sessionKey = Array.Empty<byte>();

            public byte[] CreateStepM3Payload(byte[] salt, byte[] serverPublicKey, string pin)
            {
                var random = new SecureRandom();
                var group = Srp6StandardGroups.rfc5054_3072;
                _srpClient.Init(group.N, group.G, new Sha512Digest(), random);

                var identity = Encoding.ASCII.GetBytes(PairSetupIdentity);
                var password = Encoding.ASCII.GetBytes(pin);
                var clientPublic = _srpClient.GenerateClientCredentials(salt, identity, password).ToByteArrayUnsigned();
                var serverBigInteger = new BigInteger(1, serverPublicKey);
                _srpClient.CalculateSecret(serverBigInteger);
                var clientProof = _srpClient.CalculateClientEvidenceMessage().ToByteArrayUnsigned();

                return HapTlv8.Encode(
                    ((byte)HapTlvType.State, new byte[] { 0x03 }),
                    ((byte)HapTlvType.PublicKey, clientPublic),
                    ((byte)HapTlvType.Proof, clientProof));
            }

            public bool VerifyServerProof(byte[] proof)
            {
                var ok = _srpClient.VerifyServerEvidenceMessage(new BigInteger(1, proof));
                if (!ok)
                {
                    return false;
                }

                var session = _srpClient.CalculateSessionKey().ToByteArrayUnsigned();
                _sessionKey = session.Length >= 64 ? session : LeftPad(session, 64);
                return true;
            }

            public byte[] GetSessionKey()
            {
                return _sessionKey.ToArray();
            }

            private static byte[] LeftPad(byte[] input, int size)
            {
                if (input.Length >= size)
                {
                    return input;
                }

                var output = new byte[size];
                Buffer.BlockCopy(input, 0, output, size - input.Length, input.Length);
                return output;
            }
        }
    }
}
