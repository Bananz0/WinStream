using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
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
        private string _session; // Session ID for RTSP
        private string _transport; // Transport header
        private string _userAgent = "RTSPClient";

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
                             $"CSeq: {++_cSeq}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendAnnounce(string target, string sdp)
        {
            string request = $"ANNOUNCE {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             "Content-Type: application/sdp\r\n" +
                             $"Content-Length: {sdp.Length}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "\r\n" +
                             sdp;
            return await SendRequest(request);
        }

        public async Task<string> SendSetup(string target, int clientRtpPort, int controlPort, int timingPort)
        {
            string request = $"SETUP {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Transport: RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={controlPort};timing_port={timingPort}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n";
            var response = await SendRequest(request);
            ParseTransport(response);
            return response;
        }

        public async Task<string> SendRecord(string target, string session)
        {
            string request = $"RECORD {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Session: {session}\r\n" +
                             "Range: npt=0-\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendTeardown(string target, string session)
        {
            string request = $"TEARDOWN {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Session: {session}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendSetParameter(string target, string parameter)
        {
            string request = $"SET_PARAMETER {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             "Content-Type: text/parameters\r\n" +
                             $"Content-Length: {parameter.Length}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "\r\n" +
                             parameter;
            return await SendRequest(request);
        }

        public async Task<string> SendAuthSetup(string target, byte[] data)
        {
            string request = $"POST {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             "Content-Type: application/octet-stream\r\n" +
                             $"Content-Length: {data.Length}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "\r\n" +
                             Convert.ToBase64String(data); // Assuming the data needs to be base64 encoded
            return await SendRequest(request);
        }

        public async Task<string> SendFlush(string target, string session)
        {
            string request = $"FLUSH {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Session: {session}\r\n" +
                             "RTP-Info: seq=0;rtptime=0\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
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
                var response = new StringBuilder();
                string line;
                while (!string.IsNullOrWhiteSpace(line = await _reader.ReadLineAsync()))
                {
                    response.AppendLine(line);
                    if (line.StartsWith("Session: "))
                    {
                        _session = line.Substring(9).Trim();
                    }
                }
                return response.ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading response: {ex.Message}");
                return null;
            }
        }

        private void ParseTransport(string response)
        {
            var lines = response.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("Transport: "))
                {
                    _transport = line.Substring(11).Trim();
                }
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
