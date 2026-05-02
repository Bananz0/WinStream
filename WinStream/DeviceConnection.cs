using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WinStream.Audio;

namespace WinStream.Network
{
    /// <summary>
    /// Result of a successful AirPlay connection containing session info
    /// </summary>
    public class AirPlayConnectionResult
    {
        public bool Success { get; set; }
        public bool RequiresPin { get; set; }
        public string Message { get; set; }
        public string DeviceIp { get; set; }
        public int ServerPort { get; set; }
        public int ControlPort { get; set; }
        public int TimingPort { get; set; }
        public int AudioLatency { get; set; }
        public string SessionId { get; set; }
        public AudioCodec AudioCodec { get; set; }
        public AudioSessionManager AudioSession { get; set; }
    }

    public static class DeviceConnection
    {
        private const string ClientSessionId = "3413821438";
        private const int FramesPerPacket = 352;
        private const string AudioCodecEnvVar = "WINSTREAM_AUDIO_CODEC";
        private static RSA _rsaPublicKey;
        private static string _storedAppleChallenge; // Store the challenge we send
        
        // Current active audio session
        private static AudioSessionManager _currentAudioSession;
        
        /// <summary>
        /// Gets the current active audio session, if any
        /// </summary>
        public static AudioSessionManager CurrentAudioSession => _currentAudioSession;

        /// <summary>
        /// Connects to an AirPlay device and optionally starts audio streaming
        /// </summary>
        public static async Task<AirPlayConnectionResult> ConnectAndStreamAsync(
            DeviceInfo deviceInfo,
            bool startStreaming = true,
            bool preferAirPlay2Auth = false)
        {
            var result = await ConnectToAirPlayServerAsync(deviceInfo, preferAirPlay2Auth);
            
            if (result.Success && startStreaming)
            {
                try
                {
                    Logger.LogMessage("Initializing audio session...", "connection");
                    
                    if (!await result.AudioSession.InitializeAsync())
                    {
                        result.Message = "Connected but failed to initialize audio capture";
                        Logger.LogMessage(result.Message, "connection");
                        return result;
                    }

                    Logger.LogMessage("Starting audio streaming...", "connection");
                    await result.AudioSession.StartStreamingAsync();
                    
                    _currentAudioSession = result.AudioSession;
                    result.Message = "Connected and streaming";
                    Logger.LogMessage("Audio streaming started successfully!", "connection");
                }
                catch (Exception ex)
                {
                    result.Message = $"Connected but streaming failed: {ex.Message}";
                    Logger.LogException(ex);
                }
            }
            
            return result;
        }

        /// <summary>
        /// Stops the current audio streaming session
        /// </summary>
        public static async Task StopStreamingAsync()
        {
            if (_currentAudioSession != null)
            {
                await _currentAudioSession.StopStreamingAsync();
                _currentAudioSession.Dispose();
                _currentAudioSession = null;
                Logger.LogMessage("Audio streaming stopped", "connection");
            }
        }

        /// <summary>
        /// Connects to an AirPlay server and returns connection details
        /// </summary>
        public static async Task<AirPlayConnectionResult> ConnectToAirPlayServerAsync(
            DeviceInfo deviceInfo,
            bool preferAirPlay2Auth = false)
        {
            // Check what type of device/key we're dealing with
            if (deviceInfo.IsAirPlay2Device)
            {
                var info = "Device reports a non-RSA public key (likely AirPlay 2). Will attempt unencrypted connection.";
                Debug.WriteLine(info);
                Logger.LogMessage(info, "authentication");
            }
            else if (!deviceInfo.HasRsaPublicKey)
            {
                var warning = "Device does not have a valid RSA public key. Will attempt unencrypted connection.";
                Debug.WriteLine(warning);
                Logger.LogMessage(warning, "authentication");
            }

            string ipAddress = NormalizeIpAddress(deviceInfo.IPAddress);

            if (!ValidatePort(deviceInfo.Port))
            {
                return new AirPlayConnectionResult { Success = false, Message = "Invalid port number" };
            }

            // Only create RSA instance if the device has an RSA public key
            _rsaPublicKey = null;
            if (deviceInfo.HasRsaPublicKey)
            {
                _rsaPublicKey = RSA.Create();
                _rsaPublicKey.ImportParameters(deviceInfo.RsaPublicKey.Value);
                Logger.LogMessage($"Imported RSA public key for device: {deviceInfo.DisplayName}", "authentication");
            }
            else
            {
                Logger.LogMessage($"No RSA key available for {deviceInfo.DisplayName} - using unencrypted mode", "authentication");
            }

            try
            {
                if (preferAirPlay2Auth && deviceInfo.IsAirPlay2Device)
                {
                    Logger.LogMessage(
                        "Prefer AirPlay 2 auth is enabled; starting with forced auth flow.",
                        "authentication");
                    var forcedFirst = await RunConnectionAttemptAsync(deviceInfo, ipAddress, forceAirPlay2Auth: true);
                    return forcedFirst;
                }

                var firstAttempt = await RunConnectionAttemptAsync(deviceInfo, ipAddress, forceAirPlay2Auth: false);
                if (!firstAttempt.Success &&
                    deviceInfo.IsAirPlay2Device &&
                    ShouldRetryWithForcedAirPlay2Auth(firstAttempt.Message))
                {
                    Logger.LogMessage(
                        "First ANNOUNCE attempt timed out on AP2 receiver. Retrying with a fresh RTSP connection and forced AirPlay 2 auth.",
                        "authentication");
                    var retryAttempt = await RunConnectionAttemptAsync(deviceInfo, ipAddress, forceAirPlay2Auth: true);
                    return retryAttempt;
                }

                return firstAttempt;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to connect or error during RTSP communication: {ex.Message}";
                Debug.WriteLine(errorMsg);
                Logger.LogException(ex);
                Logger.LogMessage(errorMsg, "authentication");
                return new AirPlayConnectionResult { Success = false, Message = errorMsg };
            }
            finally
            {
                _rsaPublicKey?.Dispose();
            }
        }

        private static async Task<AirPlayConnectionResult> RunConnectionAttemptAsync(
            DeviceInfo deviceInfo,
            string ipAddress,
            bool forceAirPlay2Auth)
        {
            using var rtspClient = new RtspClient(ipAddress, deviceInfo.Port, _rsaPublicKey);
            Logger.LogMessage(
                $"Creating RTSP client for {deviceInfo.DisplayName} at {ipAddress}:{deviceInfo.Port} (forceAuth={forceAirPlay2Auth})",
                "authentication");
            return await ExecuteRtspSequenceAsync(rtspClient, ipAddress, deviceInfo, forceAirPlay2Auth);
        }
        
        /// <summary>
        /// Legacy method for backward compatibility
        /// </summary>
        public static async Task<string> ConnectToAirPlayServer(DeviceInfo deviceInfo)
        {
            var result = await ConnectToAirPlayServerAsync(deviceInfo);
            return result.Success ? $"Success: {result.Message}" : result.Message;
        }

        private static string NormalizeIpAddress(string ipAddress)
        {
            if (ipAddress.StartsWith("[::ffff:]"))
            {
                Debug.WriteLine($"Adjusting IP address format: {ipAddress}");
                ipAddress = ipAddress.Replace("[::ffff:", "").Replace("]", "");
                Debug.WriteLine($"Adjusted IP address: {ipAddress}");
            }
            return ipAddress;
        }

        private static bool ValidatePort(int port)
        {
            if (port <= 0 || port > 65535)
            {
                Debug.WriteLine($"Invalid port number: {port}. Port must be between 1 and 65535.");
                return false;
            }
            return true;
        }

        private static async Task<AirPlayConnectionResult> ExecuteRtspSequenceAsync(
            RtspClient rtspClient,
            string ipAddress,
            DeviceInfo deviceInfo,
            bool forceAirPlay2Auth)
        {
            var deviceName = deviceInfo.DisplayName;
            var selectedCodec = ResolveAudioCodec(deviceInfo);
            var result = new AirPlayConnectionResult
            {
                DeviceIp = ipAddress,
                ControlPort = 6001,
                TimingPort = 6002,
                AudioCodec = selectedCodec
            };
            var airPlay2AuthCompleted = false;

            Logger.LogMessage($"Attempting to connect to {deviceName}...", "authentication");

            string optionsResponse = await SendOptions(rtspClient);
            if (!IsSuccessResponse(optionsResponse) && deviceInfo.IsAirPlay2Device && IsLikelyAuthRequired(optionsResponse))
            {
                Logger.LogMessage("OPTIONS indicates authentication is required; starting AirPlay 2 auth.", "authentication");
                var authResult = await AirPlay2AuthService.AuthenticateAndEnableEncryptionAsync(rtspClient, deviceInfo);
                if (!authResult.Success)
                {
                    result.RequiresPin = authResult.PinRequired;
                    result.Message = authResult.Message;
                    Logger.LogMessage(result.Message, "authentication");
                    return result;
                }

                Logger.LogMessage(authResult.Message, "authentication");
                airPlay2AuthCompleted = true;
                optionsResponse = await SendOptions(rtspClient);
            }

            if (!IsSuccessResponse(optionsResponse))
            {
                result.Message = await DescribeStageFailureAsync("OPTIONS", optionsResponse, deviceInfo, ipAddress, deviceInfo.Port);
                Logger.LogMessage(result.Message, "authentication");
                return result;
            }
            Logger.LogMessage("OPTIONS request succeeded", "authentication");

            if (deviceInfo.IsAirPlay2Device && forceAirPlay2Auth && !airPlay2AuthCompleted)
            {
                Logger.LogMessage("Forced AirPlay 2 auth enabled for this attempt (post-OPTIONS).", "authentication");
                var forcedAuthResult = await AirPlay2AuthService.AuthenticateAndEnableEncryptionAsync(rtspClient, deviceInfo);
                if (!forcedAuthResult.Success)
                {
                    result.RequiresPin = forcedAuthResult.PinRequired;
                    result.Message = forcedAuthResult.Message;
                    Logger.LogMessage(result.Message, "authentication");
                    return result;
                }

                Logger.LogMessage(forcedAuthResult.Message, "authentication");
                airPlay2AuthCompleted = true;

                optionsResponse = await SendOptions(rtspClient);
                if (!IsSuccessResponse(optionsResponse))
                {
                    result.Message = await DescribeStageFailureAsync("OPTIONS", optionsResponse, deviceInfo, ipAddress, deviceInfo.Port);
                    Logger.LogMessage(result.Message, "authentication");
                    return result;
                }
            }

            _storedAppleChallenge = GenerateAppleChallenge();
            Logger.LogMessage($"Generated Apple-Challenge: {_storedAppleChallenge}", "authentication");
            
            Logger.LogMessage($"Attempting ANNOUNCE with codec: {selectedCodec}", "authentication");
            string announceResponse = await SendAnnounce(rtspClient, ipAddress, _storedAppleChallenge, selectedCodec);

            if (!IsSuccessResponse(announceResponse))
            {
                if (deviceInfo.IsAirPlay2Device &&
                    announceResponse.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase))
                {
                    result.Message = await DescribeStageFailureAsync("ANNOUNCE", announceResponse, deviceInfo, ipAddress, deviceInfo.Port);
                    Logger.LogMessage(result.Message, "authentication");
                    Logger.LogMessage($"Primary ANNOUNCE response:\n{announceResponse}", "authentication");
                    return result;
                }

                var fallbackCodec = selectedCodec == AudioCodec.AppleLossless
                    ? AudioCodec.L16
                    : AudioCodec.AppleLossless;

                Logger.LogMessage(
                    $"ANNOUNCE failed with codec {selectedCodec}. Retrying with fallback codec {fallbackCodec}.",
                    "authentication");

                var fallbackResponse = await SendAnnounce(rtspClient, ipAddress, _storedAppleChallenge, fallbackCodec);
                if (!IsSuccessResponse(fallbackResponse))
                {
                    result.Message = await DescribeStageFailureAsync("ANNOUNCE", fallbackResponse, deviceInfo, ipAddress, deviceInfo.Port);
                    Logger.LogMessage(result.Message, "authentication");
                    Logger.LogMessage($"Primary ANNOUNCE response:\n{announceResponse}", "authentication");
                    Logger.LogMessage($"Fallback ANNOUNCE response:\n{fallbackResponse}", "authentication");
                    return result;
                }

                result.AudioCodec = fallbackCodec;
                announceResponse = fallbackResponse;
                Logger.LogMessage($"ANNOUNCE fallback succeeded with codec: {fallbackCodec}", "authentication");
            }
            Logger.LogMessage("ANNOUNCE request succeeded", "authentication");

            string setupResponse = await SendSetup(rtspClient, ipAddress);
            if (!IsSuccessResponse(setupResponse))
            {
                result.Message = await DescribeStageFailureAsync("SETUP", setupResponse, deviceInfo, ipAddress, deviceInfo.Port);
                Logger.LogMessage(result.Message, "authentication");
                return result;
            }
            Logger.LogMessage("SETUP request succeeded", "authentication");

            // Parse server port from SETUP response
            result.ServerPort = ParseServerPort(setupResponse);
            result.ControlPort = ParseTransportPort(setupResponse, "control_port", result.ControlPort);
            result.TimingPort = ParseTransportPort(setupResponse, "timing_port", result.TimingPort);
            result.SessionId = ParseSessionId(setupResponse);
            Logger.LogMessage(
                $"Parsed server_port: {result.ServerPort}, control_port: {result.ControlPort}, timing_port: {result.TimingPort}, session: {result.SessionId}",
                "authentication");

            string recordResponse = await SendRecord(rtspClient, ipAddress, setupResponse);
            if (!string.IsNullOrEmpty(recordResponse) && !recordResponse.Contains("200 OK"))
            {
                result.Message = "RECORD request failed.";
                Logger.LogMessage(result.Message, "authentication");
                return result;
            }
            Logger.LogMessage("RECORD request succeeded", "authentication");

            // Parse audio latency from RECORD response
            result.AudioLatency = ParseAudioLatency(recordResponse);
            Logger.LogMessage($"Audio latency: {result.AudioLatency} samples", "authentication");

            // Create audio session manager
            result.AudioSession = new AudioSessionManager(
                ipAddress,
                result.ServerPort,
                result.ControlPort,
                result.TimingPort,
                result.AudioLatency,
                result.AudioCodec);

            result.Success = true;
            result.Message = $"Successfully connected to {deviceName}!";
            Logger.LogMessage(result.Message, "authentication");
            
            return result;
        }

        private static bool IsSuccessResponse(string response)
        {
            return !string.IsNullOrWhiteSpace(response) &&
                   response.Contains("RTSP/1.0 200 OK", StringComparison.OrdinalIgnoreCase);
        }

        private static int? ParseRtspStatusCode(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return null;
            }

            var firstLine = response.Split('\n').FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return null;
            }

            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var statusCode))
            {
                return statusCode;
            }

            return null;
        }

        private static bool IsLikelyAuthRequired(string response)
        {
            var statusCode = ParseRtspStatusCode(response);
            return statusCode == 401 || statusCode == 403;
        }

        private static bool ShouldRetryWithForcedAirPlay2Auth(string message)
        {
            return !string.IsNullOrWhiteSpace(message) &&
                   message.Contains("ANNOUNCE timed out on a non-RSA receiver", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<string> DescribeStageFailureAsync(string stage, string response, DeviceInfo deviceInfo, string ipAddress, int port)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return $"{stage} request failed: no response from receiver (timed out or connection closed).";
            }

            if (response.StartsWith("RTSP_ERROR ", StringComparison.OrdinalIgnoreCase))
            {
                if (response.Contains("TimeoutException", StringComparison.OrdinalIgnoreCase) &&
                    stage.Equals("ANNOUNCE", StringComparison.OrdinalIgnoreCase) &&
                    deviceInfo.IsAirPlay2Device)
                {
                    return "ANNOUNCE timed out on a non-RSA receiver. This endpoint likely requires AirPlay 2 authentication/encryption (pair-setup/pair-verify) before ANNOUNCE.";
                }

                return $"{stage} request failed: {response}.";
            }

            var statusCode = ParseRtspStatusCode(response);
            if (statusCode == 403)
            {
                if (deviceInfo.IsAirPlay2Device)
                {
                    var pairVerifyProbe = await AirPlay2AuthService.TryPairVerifyProbeAsync(ipAddress, port);
                    return pairVerifyProbe.Success
                        ? $"Receiver rejected unauthenticated {stage} (403). Pair-verify bootstrap responded; full AirPlay 2 pair-setup/pair-verify credentials are required."
                        : $"Receiver rejected unauthenticated {stage} (403). Pair-verify probe failed: {pairVerifyProbe.Message}";
                }

                return $"Receiver rejected unauthenticated {stage} (403).";
            }

            if (statusCode.HasValue)
            {
                return $"{stage} request failed with RTSP status {statusCode.Value}.";
            }

            return $"{stage} request failed. Response could not be parsed.";
        }

        private static async Task<string> SendOptions(RtspClient rtspClient)
        {
            Logger.LogMessage("Sending OPTIONS request...", "authentication");
            string optionsResponse = await rtspClient.SendOptions();
            Logger.LogMessage($"OPTIONS Response:\n{optionsResponse}", "authentication");
            return optionsResponse;
        }

        private static async Task<string> SendAnnounce(RtspClient rtspClient, string ipAddress, string appleChallenge, AudioCodec audioCodec)
        {
            Logger.LogMessage("Preparing SDP data for ANNOUNCE request...", "authentication");
            
            string sdp;
            
            // Check if we have a valid RSA key for encryption
            if (_rsaPublicKey != null)
            {
                // Encrypted mode - generate AES key and encrypt with RSA
                Logger.LogMessage("Using encrypted mode (RSA key available)", "authentication");
                byte[] aesKeyBytes, aesIvBytes;
                string aesKey = GenerateBase64AesKey(out aesKeyBytes);
                string aesIv = GenerateBase64AesIv(out aesIvBytes);
                string encryptedAesKey = EncryptAesKeyWithRsa(aesKeyBytes, _rsaPublicKey);
                sdp = PrepareSdpData(rtspClient.LocalIp, ipAddress, ClientSessionId, FramesPerPacket, encryptedAesKey, aesIv, audioCodec);
            }
            else
            {
                // Unencrypted mode - no RSA key available
                Logger.LogMessage("Using unencrypted mode (no RSA key)", "authentication");
                sdp = PrepareSdpDataUnencrypted(rtspClient.LocalIp, ipAddress, ClientSessionId, FramesPerPacket, audioCodec);
            }
            
            Logger.LogMessage($"SDP Data:\n{sdp}", "authentication");
            Logger.LogMessage("Sending ANNOUNCE request...", "authentication");
            var sessionUri = $"rtsp://{rtspClient.LocalIp}/{ClientSessionId}";
            string announceResponse = await rtspClient.SendAnnounce(sessionUri, sdp, appleChallenge);
            Logger.LogMessage($"ANNOUNCE Response:\n{announceResponse}", "authentication");
            return announceResponse;
        }

        private static async Task<string> SendSetup(RtspClient rtspClient, string ipAddress)
        {
            Debug.WriteLine("Sending SETUP request...");
            Logger.LogMessage("Sending SETUP request...", "authentication");
            // Use the same session URL as ANNOUNCE, AirPlay expects this format
            // Control port (6001) is for RTCP control messages
            // Timing port (6002) is for NTP-like timing synchronization
            var sessionUri = $"rtsp://{rtspClient.LocalIp}/{ClientSessionId}";
            string setupResponse = await rtspClient.SendSetup(sessionUri, 6001, 6002);
            Debug.WriteLine($"SETUP Response: {setupResponse}");
            Logger.LogMessage($"SETUP Response:\n{setupResponse}", "authentication");
            return setupResponse;
        }

        private static async Task<string> SendRecord(RtspClient rtspClient, string ipAddress, string setupResponse)
        {
            Debug.WriteLine("Sending RECORD request...");
            string session = ParseSessionId(setupResponse);
            if (string.IsNullOrEmpty(session))
            {
                Debug.WriteLine("Session ID not found in SETUP response.");
                return string.Empty;
            }
            var sessionUri = $"rtsp://{rtspClient.LocalIp}/{ClientSessionId}";
            string recordResponse = await rtspClient.SendRecord(sessionUri, session);
            Debug.WriteLine($"RECORD Response: {recordResponse}");
            return recordResponse;
        }

        private static string PrepareSdpData(string localIpAddress, string remoteIpAddress, string clientSessionId, int framesPerPacket, string aesKey, string aesIv, AudioCodec audioCodec)
        {
            Debug.WriteLine($"rsaaeskey: {aesKey}");
            Debug.WriteLine($"aesiv: {aesIv}");

            var codecLines = BuildSdpCodecLines(framesPerPacket, audioCodec);
            return $"v=0\r\n" +
                   $"o=iTunes {clientSessionId} 0 IN IP4 {localIpAddress}\r\n" +
                   $"s=iTunes\r\n" +
                   $"c=IN IP4 {remoteIpAddress}\r\n" +
                   $"t=0 0\r\n" +
                   codecLines +
                   $"a=rsaaeskey:{aesKey}\r\n" +
                   $"a=aesiv:{aesIv}\r\n";
        }

        private static string PrepareSdpDataUnencrypted(string localIpAddress, string remoteIpAddress, string clientSessionId, int framesPerPacket, AudioCodec audioCodec)
        {
            // SDP without encryption - for Shairport-Sync installations without encryption enabled
            var codecLines = BuildSdpCodecLines(framesPerPacket, audioCodec);
            return $"v=0\r\n" +
                   $"o=iTunes {clientSessionId} 0 IN IP4 {localIpAddress}\r\n" +
                   $"s=iTunes\r\n" +
                   $"c=IN IP4 {remoteIpAddress}\r\n" +
                   $"t=0 0\r\n" +
                   codecLines;
        }

        private static string BuildSdpCodecLines(int framesPerPacket, AudioCodec audioCodec)
        {
            if (audioCodec == AudioCodec.L16)
            {
                return "m=audio 0 RTP/AVP 96\r\n" +
                       "a=rtpmap:96 L16/44100/2\r\n" +
                       $"a=fmtp:96 {framesPerPacket} 0 16 40 10 14 2 255 0 0 44100\r\n";
            }

            return "m=audio 0 RTP/AVP 96\r\n" +
                   "a=rtpmap:96 AppleLossless\r\n" +
                   $"a=fmtp:96 {framesPerPacket} 0 16 40 10 14 2 255 0 0 44100\r\n";
        }

        private static string EncryptAesKeyWithRsa(byte[] aesKey, RSA rsaPublicKey)
        {
            var encryptedKey = rsaPublicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA1);
            return Convert.ToBase64String(encryptedKey).TrimEnd('=');
        }

        private static string ParseSessionId(string setupResponse)
        {
            string sessionIdLine = setupResponse.Split('\n').FirstOrDefault(line => line.StartsWith("Session:"));
            if (sessionIdLine != null)
            {
                var rawSession = sessionIdLine.Split(' ').Last().Trim();
                var semicolonIndex = rawSession.IndexOf(';');
                return semicolonIndex >= 0 ? rawSession.Substring(0, semicolonIndex).Trim() : rawSession;
            }
            Debug.WriteLine("Session ID not found in SETUP response.");
            return string.Empty;
        }

        private static int ParseServerPort(string setupResponse)
        {
            // Parse server_port from Transport header
            // Example: Transport: RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port=6001;timing_port=6002;server_port=6003
            var transportLine = setupResponse.Split('\n').FirstOrDefault(line => line.StartsWith("Transport:"));
            if (transportLine != null)
            {
                var parts = transportLine.Split(';');
                foreach (var part in parts)
                {
                    if (part.Trim().StartsWith("server_port="))
                    {
                        var portStr = part.Split('=').Last().Trim();
                        if (int.TryParse(portStr, out int port))
                        {
                            return port;
                        }
                    }
                }
            }
            Debug.WriteLine("Server port not found in SETUP response, using default 6003");
            return 6003; // Default AirPlay audio port
        }

        private static int ParseTransportPort(string setupResponse, string portField, int fallbackPort)
        {
            var transportLine = setupResponse.Split('\n').FirstOrDefault(line => line.StartsWith("Transport:"));
            if (transportLine != null)
            {
                var parts = transportLine.Split(';');
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (trimmed.StartsWith(portField + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        var portStr = trimmed.Split('=').Last().Trim();
                        if (int.TryParse(portStr, out var parsedPort))
                        {
                            return parsedPort;
                        }
                    }
                }
            }

            return fallbackPort;
        }

        private static int ParseAudioLatency(string recordResponse)
        {
            // Parse Audio-Latency from RECORD response
            // Example: Audio-Latency: 11025
            if (string.IsNullOrEmpty(recordResponse))
                return 11025; // Default latency

            var latencyLine = recordResponse.Split('\n').FirstOrDefault(line => line.StartsWith("Audio-Latency:"));
            if (latencyLine != null)
            {
                var latencyStr = latencyLine.Split(':').Last().Trim();
                if (int.TryParse(latencyStr, out int latency))
                {
                    return latency;
                }
            }
            Debug.WriteLine("Audio-Latency not found in RECORD response, using default 11025");
            return 11025; // Default ~250ms at 44.1kHz
        }

        private static string GenerateAppleChallenge()
        {
            var randomBytes = new byte[16]; // 128 bits
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }
            return Convert.ToBase64String(randomBytes).TrimEnd('=');
        }

        private static string GenerateBase64AesKey(out byte[] aesKey)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 128; // 128 bits
                aes.GenerateKey();
                aesKey = aes.Key;
                return Convert.ToBase64String(aes.Key).TrimEnd('=');
            }
        }

        private static string GenerateBase64AesIv(out byte[] aesIv)
        {
            using (var aes = Aes.Create())
            {
                aes.BlockSize = 128; // 128 bits
                aes.GenerateIV();
                aesIv = aes.IV;
                return Convert.ToBase64String(aes.IV).TrimEnd('=');
            }
        }

        private static AudioCodec ResolveAudioCodec(DeviceInfo deviceInfo)
        {
            var configured = Environment.GetEnvironmentVariable(AudioCodecEnvVar);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var normalized = configured.Trim();
                if (normalized.Equals("l16", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogMessage("Using RTP codec mode: L16 (environment override)", "authentication");
                    return AudioCodec.L16;
                }

                if (normalized.Equals("alac", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogMessage("Using RTP codec mode: ALAC (environment override)", "authentication");
                    return AudioCodec.AppleLossless;
                }
            }

            if (deviceInfo != null)
            {
                if (deviceInfo.IsAirPlay2Device && deviceInfo.SupportsL16)
                {
                    Logger.LogMessage("AirPlay 2 receiver detected; preferring L16 for compatibility.", "authentication");
                    return AudioCodec.L16;
                }

                if (deviceInfo.SupportsAlac)
                {
                    return AudioCodec.AppleLossless;
                }

                if (deviceInfo.SupportsL16)
                {
                    Logger.LogMessage("Receiver does not advertise ALAC (`cn=1` absent); using L16.", "authentication");
                    return AudioCodec.L16;
                }
            }

            Logger.LogMessage("Receiver codec capabilities unknown; defaulting to L16.", "authentication");
            return AudioCodec.L16;
        }
    }
}
