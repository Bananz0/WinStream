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
                string sdp = "v=0\r\n" +
                             "o=- 0 0 IN IP4 127.0.0.1\r\n" +
                             "s=Unnamed\r\n" +
                             "i=N/A\r\n" +
                             "c=IN IP4 " + ipAddress + "\r\n" +
                             "t=0 0\r\n" +
                             "m=audio 0 RTP/AVP 96\r\n" +
                             "a=rtpmap:96 L16/44100/2\r\n" +
                             "a=control:track1\r\n";
                Debug.WriteLine("Sending ANNOUNCE request...");
                string announceResponse = await rtspClient.SendAnnounce("rtsp://" + ipAddress + "/stream", sdp);
                Debug.WriteLine($"ANNOUNCE Response: {announceResponse}");

                Debug.WriteLine("Sending SETUP request...");
                string setupResponse = await rtspClient.SendSetup("rtsp://" + ipAddress + "/stream/track1", 6000);
                Debug.WriteLine($"SETUP Response: {setupResponse}");

                Debug.WriteLine("Sending PLAY request...");
                string session = ParseSessionId(setupResponse);
                string playResponse = await rtspClient.SendPlay("rtsp://" + ipAddress + "/stream", session);
                Debug.WriteLine($"PLAY Response: {playResponse}");
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

        private static string ParseSessionId(string setupResponse)
        {
            string sessionIdLine = setupResponse.Split('\n').FirstOrDefault(line => line.StartsWith("Session:"));
            if (sessionIdLine != null)
            {
                return sessionIdLine.Split(' ').Last().Trim();
            }
            Debug.WriteLine("Session ID not found in SETUP response.");
            return string.Empty; // Or handle this case specifically
        }

        public static async Task PerformHandshake(TcpClient client)
        {
            using var networkStream = client.GetStream();
            using var writer = new StreamWriter(networkStream);
            using var reader = new StreamReader(networkStream);

            // Example handshake message; replace with actual required messages.
            await writer.WriteLineAsync("Example handshake message");
            await writer.FlushAsync();

            var response = await reader.ReadLineAsync();
            Debug.WriteLine($"Received handshake response: {response}");
        }
    }
}
