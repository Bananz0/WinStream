using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
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
        private int _cSeq;
        private string _session;
        private string _transport;
        private string _userAgent = "RTSPClient";
        private string _localIp;

        public RtspClient(string serverIp, int serverPort)
        {
            _client = new TcpClient();
            _client.Connect(IPAddress.Parse(serverIp), serverPort);
            _stream = _client.GetStream();
            _writer = new StreamWriter(_stream) { AutoFlush = true };
            _reader = new StreamReader(_stream);
            _cSeq = 0;
            _localIp = ((IPEndPoint)_client.Client.LocalEndPoint).Address.ToString();
        }

        public int GetSocket() => _client.Client.Handle.ToInt32();

        public bool IsConnected() => _client.Connected && IsSane();

        private bool IsSane()
        {
            try
            {
                return !_client.Client.Poll(1, SelectMode.SelectRead) || _client.Client.Available != 0;
            }
            catch (SocketException) { return false; }
        }

        public async Task<bool> Connect(IPAddress local, IPAddress host, int destPort, string sid)
        {
            try
            {
                _session = null;
                await _client.ConnectAsync(host, destPort);
                _localIp = ((IPEndPoint)_client.Client.LocalEndPoint).Address.ToString();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to connect: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> Disconnect()
        {
            try
            {
                if (_client.Connected)
                {
                    await SendTeardown("rtsp://");
                    _client.Close();
                }

                _session = null;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to disconnect: {ex.Message}");
                return false;
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
                             $"Content-Length: {Encoding.UTF8.GetByteCount(sdp)}\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n" +
                             sdp;
            return await SendRequest(request);
        }

        public async Task<string> SendSetup(string target, int clientRtpPort, int controlPort, int timingPort)
        {
            string request = $"SETUP {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Transport: RTP/AVP/UDP;unicast;client_port={clientRtpPort}-{controlPort};mode=record\r\n" +
                             $"User-Agent: {_userAgent}\r\n" +
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

        public async Task<string> SendTeardown(string target)
        {
            string request = $"TEARDOWN {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Session: {_session}\r\n" +
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
                             Convert.ToBase64String(data);
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

        public async Task<string> SendRequest(string request)
        {
            try
            {
                Debug.WriteLine($"Sending request: {request}");
                await _writer.WriteAsync(request);
                return await ReadResponseAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending request: {ex.Message}");
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
                Debug.WriteLine($"Received response: {response}");
                return response.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading response: {ex.Message}");
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

        public async Task PerformHandshake(TcpClient client)
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
