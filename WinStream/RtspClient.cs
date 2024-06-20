using System;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WinStream.Network
{
    public class RtspClient : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StreamWriter _writer;
        private readonly StreamReader _reader;
        private int _cSeq; // Sequence number for RTSP requests

        public RtspClient(string serverIp, int serverPort)
        {
            _client = new TcpClient(serverIp, serverPort);
            _stream = _client.GetStream();
            _writer = new StreamWriter(_stream) { AutoFlush = true };
            _reader = new StreamReader(_stream);
            _cSeq = 0;
        }

        public async Task<string> SendOptions(string target = "*")
        {
            string request = $"OPTIONS {target} RTSP/1.0\r\n" +
                             "CSeq: " + (++_cSeq) + "\r\n" +
                             "User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendAnnounce(string target, string sdp)
        {
            string request = $"ANNOUNCE {target} RTSP/1.0\r\n" +
                             "CSeq: " + (++_cSeq) + "\r\n" +
                             "Content-Type: application/sdp\r\n" +
                             "Content-Length: " + sdp.Length + "\r\n" +
                             "User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "\r\n" +
                             sdp;
            return await SendRequest(request);
        }

        public async Task<string> SendSetup(string target, int clientRtpPort, int controlPort, int timingPort)
        {
            string request = $"SETUP {target} RTSP/1.0\r\n" +
                             "CSeq: " + (++_cSeq) + "\r\n" +
                             "Transport: RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port=" + controlPort + ";timing_port=" + timingPort + "\r\n" +
                             "User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendRecord(string target, string session)
        {
            string request = $"RECORD {target} RTSP/1.0\r\n" +
                             "CSeq: " + (++_cSeq) + "\r\n" +
                             "Session: " + session + "\r\n" +
                             "Range: npt=0-\r\n" +
                             "User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        private async Task<string> SendRequest(string request)
        {
            try
            {
                await _writer.WriteAsync(request);
                return await ReadResponseAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending request: {ex.Message}");
                return null;
            }
        }

        private async Task<string> ReadResponseAsync()
        {
            try
            {
                return await _reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading response: {ex.Message}");
                return null;
            }
        }

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _stream?.Dispose();
            _client?.Close();
            _client?.Dispose();
        }
    }
}
