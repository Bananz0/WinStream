using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace WinStream.Network
{
    public static class DeviceConnection
    {
        private const string ClientSessionId = "3413821438";
        private const int FramesPerPacket = 352;
        private static RSA _rsaPublicKey;

        public static async Task ConnectToAirPlayServer(string ipAddress, int rtspPort, RSA rsaPublicKey)
        {
            _rsaPublicKey = rsaPublicKey;

            ipAddress = NormalizeIpAddress(ipAddress);

            if (!ValidatePort(rtspPort))
            {
                return;
            }

            using var rtspClient = new RtspClient(ipAddress, rtspPort, rsaPublicKey);
            Debug.WriteLine($"Creating RTSP client for IP {ipAddress} on port {rtspPort}");

            try
            {
                await ExecuteRtspSequence(rtspClient, ipAddress);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to connect or error during RTSP communication: {ex.Message}");
                Logger.LogException(ex);
            }
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

        private static async Task ExecuteRtspSequence(RtspClient rtspClient, string ipAddress)
        {
            Debug.WriteLine("Attempting to connect to RAOP server...");

            string optionsResponse = await SendOptions(rtspClient);
            if (!optionsResponse.Contains("RTSP/1.0 200 OK"))
            {
                Debug.WriteLine("OPTIONS request failed.");
                return;
            }

            string appleChallenge = GenerateAppleChallenge();
            string announceResponse = await SendAnnounce(rtspClient, ipAddress, appleChallenge);
            if (!announceResponse.Contains("RTSP/1.0 200 OK"))
            {
                Debug.WriteLine("ANNOUNCE request failed.");
                return;
            }

            string setupResponse = await SendSetup(rtspClient, ipAddress);
            if (!setupResponse.Contains("RTSP/1.0 200 OK"))
            {
                Debug.WriteLine("SETUP request failed.");
                return;
            }

            await SendRecord(rtspClient, ipAddress, setupResponse);

            Debug.WriteLine("RTSP sequence completed successfully.");
        }

        private static async Task<string> SendOptions(RtspClient rtspClient)
        {
            Debug.WriteLine("Sending OPTIONS request...");
            string optionsResponse = await rtspClient.SendOptions();
            Debug.WriteLine($"OPTIONS Response: {optionsResponse}");
            return optionsResponse;
        }

        private static async Task<string> SendAnnounce(RtspClient rtspClient, string ipAddress, string appleChallenge)
        {
            Debug.WriteLine("Preparing SDP data for ANNOUNCE request...");
            byte[] aesKeyBytes, aesIvBytes;
            string aesKey = GenerateBase64AesKey(out aesKeyBytes);
            string aesIv = GenerateBase64AesIv(out aesIvBytes);
            string encryptedAesKey = EncryptAesKeyWithRsa(aesKeyBytes, _rsaPublicKey);
            string sdp = PrepareSdpData(rtspClient.LocalIp, ipAddress, ClientSessionId, FramesPerPacket, encryptedAesKey, aesIv);
            Debug.WriteLine($"SDP Data: {sdp}");
            Debug.WriteLine("Sending ANNOUNCE request...");
            string announceResponse = await rtspClient.SendAnnounce($"rtsp://{ipAddress}/{ClientSessionId}", sdp, appleChallenge);
            Debug.WriteLine($"ANNOUNCE Response: {announceResponse}");
            return announceResponse;
        }

        private static async Task<string> SendSetup(RtspClient rtspClient, string ipAddress)
        {
            Debug.WriteLine("Sending SETUP request...");
            string setupResponse = await rtspClient.SendSetup($"rtsp://{ipAddress}/stream/track1", 6000, 6001, 6002);
            Debug.WriteLine($"SETUP Response: {setupResponse}");
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
