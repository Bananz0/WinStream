using System;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Collections.Generic;

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
        private string _userAgent = "iTunes/9.2.1 (Macintosh; Intel Mac OS X 10.5.8) AppleWebKit/533.17.8";

        public string LocalIp { get; private set; }
        private string _clientInstance; // 64 random bytes in hex

        public RtspClient(string serverIp, int serverPort, RSA rsaPublicKey)
        {
            _client = new TcpClient(serverIp, serverPort);
            _stream = _client.GetStream();
            _writer = new StreamWriter(_stream) { AutoFlush = true };
            _reader = new StreamReader(_stream);
            _cSeq = 0;

            // Get local IP address
            var localEndPoint = (IPEndPoint)_client.Client.LocalEndPoint;
            LocalIp = localEndPoint.Address.ToString();

            // Generate Client-Instance
            _clientInstance = GenerateClientInstance();
        }

        private string GenerateClientInstance()
        {
            var rng = new Random();
            var bytes = new byte[64 / 2];
            rng.NextBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }

        private string GenerateAppleChallenge()
        {
            var randomBytes = new byte[16]; // 128 bits
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }
            return Convert.ToBase64String(randomBytes).TrimEnd('=');
        }

        private Dictionary<string, string> GetCommonHeaders()
        {
            return new Dictionary<string, string>
            {
                {"CSeq", (++_cSeq).ToString()},
                {"User-Agent", _userAgent},
                {"Client-Instance", _clientInstance}
            };
        }

        private string BuildRequest(string method, string target, Dictionary<string, string> headers, string body = "")
        {
            var request = new StringBuilder();
            request.AppendLine($"{method} {target} RTSP/1.0");

            foreach (var header in headers)
            {
                request.AppendLine($"{header.Key}: {header.Value}");
            }

            if (!string.IsNullOrEmpty(body))
            {
                request.AppendLine($"Content-Length: {Encoding.UTF8.GetByteCount(body)}");
            }

            request.AppendLine();
            if (!string.IsNullOrEmpty(body))
            {
                request.AppendLine(body);
            }

            return request.ToString();
        }

        public async Task<string> SendOptions(string target = "*")
        {
            var headers = GetCommonHeaders();
            headers.Add("Apple-Challenge", GenerateAppleChallenge());
            string request = BuildRequest("OPTIONS", target, headers);
            return await SendRequest(request);
        }

        public async Task<string> SendAnnounce(string target, string sdp, string appleChallenge)
        {
            var headers = GetCommonHeaders();
            headers.Add("Content-Type", "application/sdp");
            headers.Add("Apple-Challenge", appleChallenge);
            string request = BuildRequest("ANNOUNCE", target, headers, sdp);
            return await SendRequest(request);
        }

        public async Task<string> SendSetup(string target, int clientRtpPort, int controlPort, int timingPort)
        {
            var headers = GetCommonHeaders();
            headers.Add("Transport", $"RTP/AVP/UDP;unicast;client_port={clientRtpPort}");
            string request = BuildRequest("SETUP", target, headers);
            var response = await SendRequest(request);
            ParseTransport(response);
            return response;
        }

        public async Task<string> SendRecord(string target, string session)
        {
            var headers = GetCommonHeaders();
            headers.Add("Session", session);
            headers.Add("Range", "npt=0-");
            string request = BuildRequest("RECORD", target, headers);
            return await SendRequest(request);
        }

        public async Task<string> SendTeardown(string target, string session)
        {
            var headers = GetCommonHeaders();
            headers.Add("Session", session);
            string request = BuildRequest("TEARDOWN", target, headers);
            return await SendRequest(request);
        }

        public async Task<string> SendSetParameter(string target, string parameter)
        {
            var headers = GetCommonHeaders();
            headers.Add("Content-Type", "text/parameters");
            string request = BuildRequest("SET_PARAMETER", target, headers, parameter);
            return await SendRequest(request);
        }

        public async Task<string> SendAuthSetup(string target, byte[] data)
        {
            var headers = GetCommonHeaders();
            headers.Add("Content-Type", "application/octet-stream");
            string request = BuildRequest("POST", target, headers, Convert.ToBase64String(data));
            return await SendRequest(request);
        }

        public async Task<string> SendFlush(string target, string session)
        {
            var headers = GetCommonHeaders();
            headers.Add("Session", session);
            headers.Add("RTP-Info", "seq=0;rtptime=0");
            string request = BuildRequest("FLUSH", target, headers);
            return await SendRequest(request);
        }

        private async Task<string> SendRequest(string request)
        {
            try
            {
                Console.WriteLine($"Sending request:\n{request}");
                await _writer.WriteAsync(request);
                var response = await ReadResponseAsync();
                Console.WriteLine($"Received response:\n{response}");
                return response;
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
