using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
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
        private byte[] _aesKey;
        private byte[] _aesIv;

        public RtspClient(string serverIp, int serverPort)
        {
            _client = new TcpClient(serverIp, serverPort);
            _stream = _client.GetStream();
            _writer = new StreamWriter(_stream) { AutoFlush = true };
            _reader = new StreamReader(_stream);
            _cSeq = 0; // Initialize sequence number
        }

        public async Task<string> SendOptions(string target = "*")
        {
            string request = $"OPTIONS {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendAnnounce(string target, string sdp)
        {
            string request = $"ANNOUNCE {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             "Content-Type: application/sdp\r\n" +
                             $"Content-Length: {sdp.Length}\r\n" +
                             $"User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n" +
                             sdp;
            return await SendRequest(request);
        }

        public async Task<string> SendSetup(string target, int clientRtpPort)
        {
            string request = $"SETUP {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Transport: RTP/AVP/UDP;unicast;mode=record;client_port={clientRtpPort}\r\n" +
                             $"User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendRecord(string target, string session)
        {
            string request = $"RECORD {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Session: {session}\r\n" +
                             "Range: npt=0-\r\n" +
                             "User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "\r\n";
            return await SendRequest(request);
        }

        public async Task<string> SendPlay(string target, string session)
        {
            string request = $"PLAY {target} RTSP/1.0\r\n" +
                             $"CSeq: {++_cSeq}\r\n" +
                             $"Session: {session}\r\n" +
                             "User-Agent: iTunes/10.6 (Macintosh; Intel Mac OS X 10.7.3) AppleWebKit/535.18.5\r\n" +
                             "Client-Instance: 56B29BB6CB904862\r\n" +
                             "DACP-ID: 56B29BB6CB904862\r\n" +
                             "Active-Remote: 1986535575\r\n" +
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

        public void GenerateAesKeyAndIv()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                _aesKey = new byte[16];
                _aesIv = new byte[16];
                rng.GetBytes(_aesKey);
                rng.GetBytes(_aesIv);
            }
        }

        public string EncryptAesKeyWithRsa()
        {
            string rsaPublicKey = "59dE8qLieItsH1WgjrcFRKj6eUWqi+bGLOX1HL3U3GhC/j0Qg90u3sG/1CUtwC5vOYvfDmFI6oSFXi5ELabWJmT2dKHzBJKa3k9ok+8t9ucRqMd6DZHJ2YCCLlDRKSKv6kDqnw4UwPdpOMXziC/AMj3Z/lUVX1G7WSHCAWKf1zNS1eLvqr+boEjXuBOitnZ/bDzPHrTOZz0Dew0uowxf/+sG+NCK3eQJVxqcaJ/vEHKIVd2M+5qL71yJQ+87X6oV3eaYvt3zWZYD6z5vYTcrtij2VZ9Zmni/UAaHqn9JdsBWLUEpVviYnhimNVvYFZeCXg/IdTQ+x4IRdiXNv5hEew==";
            string rsaExponent = "AQAB";

            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Convert.FromBase64String(rsaPublicKey),
                    Exponent = Convert.FromBase64String(rsaExponent)
                });

                byte[] encryptedKey = rsa.Encrypt(_aesKey, RSAEncryptionPadding.OaepSHA1);
                return Convert.ToBase64String(encryptedKey);
            }
        }

        public string GetAesIvBase64()
        {
            return Convert.ToBase64String(_aesIv);
        }

        public void EncryptAudioData(byte[] data)
        {
            using (var aes = new AesManaged { Key = _aesKey, IV = _aesIv, Mode = CipherMode.CBC, Padding = PaddingMode.None })
            using (var encryptor = aes.CreateEncryptor())
            {
                encryptor.TransformBlock(data, 0, data.Length, data, 0);
            }
        }
    }
}
