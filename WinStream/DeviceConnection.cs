using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WinStream.Network
{
    public static class DeviceConnection
    {
        public static async Task ConnectToAirPlayServer(string ipAddress, int rtspPort)
        {
            if (ipAddress.StartsWith("[::ffff:]"))
            {
                Debug.WriteLine($"Adjusting IP address format: {ipAddress}");
                ipAddress = ipAddress.Replace("[::ffff:", "").Replace("]", "");
                Debug.WriteLine($"Adjusted IP address: {ipAddress}");
            }

            if (rtspPort <= 0 || rtspPort > 65535)
            {
                Debug.WriteLine($"Invalid port number: {rtspPort}. Port must be between 1 and 65535.");
                return;
            }

            Debug.WriteLine($"Creating RTSP client for IP {ipAddress} on port {rtspPort}");
            RtspClient rtspClient = new RtspClient(ipAddress, rtspPort);
            try
            {
                Debug.WriteLine("Attempting to connect to RAOP server...");

                Debug.WriteLine("Sending OPTIONS request...");
                string optionsResponse = await rtspClient.SendOptions();
                Debug.WriteLine($"OPTIONS Response: {optionsResponse}");

                Debug.WriteLine("Preparing SDP data for ANNOUNCE request...");
                string sdp = PrepareSdpData(ipAddress);
                Debug.WriteLine("Sending ANNOUNCE request...");
                string announceResponse = await rtspClient.SendAnnounce($"rtsp://{ipAddress}/3413821438", sdp);
                Debug.WriteLine($"ANNOUNCE Response: {announceResponse}");

                Debug.WriteLine("Sending SETUP request...");
                string setupResponse = await rtspClient.SendSetup($"rtsp://{ipAddress}/stream/track1", 6000, 6001, 6002);
                Debug.WriteLine($"SETUP Response: {setupResponse}");

                Debug.WriteLine("Sending RECORD request...");
                string session = ParseSessionId(setupResponse);
                string recordResponse = await rtspClient.SendRecord($"rtsp://{ipAddress}/stream", session);
                Debug.WriteLine($"RECORD Response: {recordResponse}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to connect or error during RTSP communication: {ex.Message}");
                Logger.LogException(ex);
            }
            finally
            {
                rtspClient.Close();
                Debug.WriteLine("RTSP client closed.");
            }
        }

        private static string PrepareSdpData(string ipAddress)
        {
            return "v=0\r\n" +
                   $"o=iTunes 3413821438 0 IN IP4 {ipAddress}\r\n" +
                   "s=iTunes\r\n" +
                   $"c=IN IP4 {ipAddress}\r\n" +
                   "t=0 0\r\n" +
                   "m=audio 0 RTP/AVP 96\r\n" +
                   "a=rtpmap:96 AppleLossless\r\n" +
                   "a=fmtp:96 352 0 16 40 10 14 2 255 0 0 44100\r\n" +
                   "a=fpaeskey:RlBMWQECAQAAAAA8AAAAAPFOnNe+zWb5/n4L5KZkE2AAAAAQlDx69reTdwHF9LaNmhiRURTAbcL4brYAceAkZ49YirXm62N4\r\n" +
                   "a=aesiv:5b+YZi9Ikb845BmNhaVo+Q\r\n";
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

        public static async Task PerformHandshake(TcpClient client)
        {
            using var networkStream = client.GetStream();
            using var writer = new StreamWriter(networkStream);
            using var reader = new StreamReader(networkStream);

            await writer.WriteLineAsync("Example handshake message");
            await writer.FlushAsync();

            var response = await reader.ReadLineAsync();
            Debug.WriteLine($"Received handshake response: {response}");
        }
    }
}
