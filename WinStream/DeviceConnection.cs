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
        public string Message { get; set; }
        public string DeviceIp { get; set; }
        public int ServerPort { get; set; }
        public int ControlPort { get; set; }
        public int TimingPort { get; set; }
        public int AudioLatency { get; set; }
        public string SessionId { get; set; }
        public AudioSessionManager AudioSession { get; set; }
    }

    public static class DeviceConnection
    {
        private const string ClientSessionId = "3413821438";
        private const int FramesPerPacket = 352;
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
        public static async Task<AirPlayConnectionResult> ConnectAndStreamAsync(DeviceInfo deviceInfo, bool startStreaming = true)
        {
            var result = await ConnectToAirPlayServerAsync(deviceInfo);
            
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
        public static async Task<AirPlayConnectionResult> ConnectToAirPlayServerAsync(DeviceInfo deviceInfo)
        {
            // Check what type of device/key we're dealing with
            if (deviceInfo.IsAirPlay2Device)
            {
                var info = "Device is an AirPlay 2 device with Ed25519 key. Will attempt unencrypted connection.";
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

            using var rtspClient = new RtspClient(ipAddress, deviceInfo.Port, _rsaPublicKey);
            Logger.LogMessage($"Creating RTSP client for {deviceInfo.DisplayName} at {ipAddress}:{deviceInfo.Port}", "authentication");

            try
            {
                return await ExecuteRtspSequenceAsync(rtspClient, ipAddress, deviceInfo.DisplayName);
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

        private static async Task<AirPlayConnectionResult> ExecuteRtspSequenceAsync(RtspClient rtspClient, string ipAddress, string deviceName)
        {
            var result = new AirPlayConnectionResult
            {
                DeviceIp = ipAddress,
                ControlPort = 6001,
                TimingPort = 6002
            };

            Logger.LogMessage($"Attempting to connect to {deviceName}...", "authentication");

            string optionsResponse = await SendOptions(rtspClient);
            if (!optionsResponse.Contains("RTSP/1.0 200 OK"))
            {
                result.Message = "OPTIONS request failed. Server may not support AirPlay.";
                Logger.LogMessage(result.Message, "authentication");
                return result;
            }
            Logger.LogMessage("OPTIONS request succeeded", "authentication");

            _storedAppleChallenge = GenerateAppleChallenge();
            Logger.LogMessage($"Generated Apple-Challenge: {_storedAppleChallenge}", "authentication");
            
            string announceResponse = await SendAnnounce(rtspClient, ipAddress, _storedAppleChallenge);
            if (!announceResponse.Contains("RTSP/1.0 200 OK"))
            {
                result.Message = "ANNOUNCE request failed. Authentication may be incorrect.";
                Logger.LogMessage(result.Message, "authentication");
                Logger.LogMessage($"Response was: {announceResponse}", "authentication");
                return result;
            }
            Logger.LogMessage("ANNOUNCE request succeeded", "authentication");

            string setupResponse = await SendSetup(rtspClient, ipAddress);
            if (!setupResponse.Contains("RTSP/1.0 200 OK"))
            {
                result.Message = "SETUP request failed.";
                Logger.LogMessage(result.Message, "authentication");
                return result;
            }
            Logger.LogMessage("SETUP request succeeded", "authentication");

            // Parse server port from SETUP response
            result.ServerPort = ParseServerPort(setupResponse);
            result.SessionId = ParseSessionId(setupResponse);
            Logger.LogMessage($"Parsed server_port: {result.ServerPort}, session: {result.SessionId}", "authentication");

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
                result.AudioLatency);

            result.Success = true;
            result.Message = $"Successfully connected to {deviceName}!";
            Logger.LogMessage(result.Message, "authentication");
            
            return result;
        }

        private static async Task<string> SendOptions(RtspClient rtspClient)
        {
            Logger.LogMessage("Sending OPTIONS request...", "authentication");
            string optionsResponse = await rtspClient.SendOptions();
            Logger.LogMessage($"OPTIONS Response:\n{optionsResponse}", "authentication");
            return optionsResponse;
        }

        private static async Task<string> SendAnnounce(RtspClient rtspClient, string ipAddress, string appleChallenge)
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
                sdp = PrepareSdpData(rtspClient.LocalIp, ipAddress, ClientSessionId, FramesPerPacket, encryptedAesKey, aesIv);
            }
            else
            {
                // Unencrypted mode - no RSA key available
                Logger.LogMessage("Using unencrypted mode (no RSA key)", "authentication");
                sdp = PrepareSdpDataUnencrypted(rtspClient.LocalIp, ipAddress, ClientSessionId, FramesPerPacket);
            }
            
            Logger.LogMessage($"SDP Data:\n{sdp}", "authentication");
            Logger.LogMessage("Sending ANNOUNCE request...", "authentication");
            string announceResponse = await rtspClient.SendAnnounce($"rtsp://{ipAddress}/{ClientSessionId}", sdp, appleChallenge);
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
            string setupResponse = await rtspClient.SendSetup($"rtsp://{ipAddress}/{ClientSessionId}", 6001, 6002);
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
            string recordResponse = await rtspClient.SendRecord($"rtsp://{ipAddress}/stream", session);
            Debug.WriteLine($"RECORD Response: {recordResponse}");
            return recordResponse;
        }

        private static string PrepareSdpData(string localIpAddress, string serverIpAddress, string clientSessionId, int framesPerPacket, string aesKey, string aesIv)
        {
            Debug.WriteLine($"rsaaeskey: {aesKey}");
            Debug.WriteLine($"aesiv: {aesIv}");

            return $"v=0\r\n" +
                   $"o=iTunes {clientSessionId} 0 IN IP4 {localIpAddress}\r\n" +
                   $"s=iTunes\r\n" +
                   $"c=IN IP4 {serverIpAddress}\r\n" +
                   $"t=0 0\r\n" +
                   $"m=audio 0 RTP/AVP 96\r\n" +
                   $"a=rtpmap:96 AppleLossless\r\n" +
                   $"a=fmtp:96 {framesPerPacket} 0 16 40 10 14 2 255 0 0 44100\r\n" +
                   $"a=rsaaeskey:{aesKey}\r\n" +
                   $"a=aesiv:{aesIv}\r\n";
        }

        private static string PrepareSdpDataUnencrypted(string localIpAddress, string serverIpAddress, string clientSessionId, int framesPerPacket)
        {
            // SDP without encryption - for Shairport-Sync installations without encryption enabled
            return $"v=0\r\n" +
                   $"o=iTunes {clientSessionId} 0 IN IP4 {localIpAddress}\r\n" +
                   $"s=iTunes\r\n" +
                   $"c=IN IP4 {serverIpAddress}\r\n" +
                   $"t=0 0\r\n" +
                   $"m=audio 0 RTP/AVP 96\r\n" +
                   $"a=rtpmap:96 AppleLossless\r\n" +
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
                return sessionIdLine.Split(' ').Last().Trim();
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
    }
}
