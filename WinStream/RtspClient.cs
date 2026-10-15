using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Parameters;

namespace WinStream.Network
{
    public class RtspClient : IDisposable
    {
        public const string VerboseLoggingEnvironmentVariable = "WINSTREAM_VERBOSE_RTSP";

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DefaultResponseTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan HapResponseTimeout = TimeSpan.FromSeconds(4);
        private readonly List<byte> _wireReadBuffer = new();
        private readonly List<byte> _plainReadBuffer = new();
        private readonly string _serverIp;
        private readonly int _serverPort;

        private int _cSeq;
        private string _transport;
        private readonly string _userAgent =
            "iTunes/9.2.1 (Macintosh; Intel Mac OS X 10.5.8) AppleWebKit/533.17.8";

        private bool _hapEncryptionEnabled;
        private byte[] _hapWriteKey = Array.Empty<byte>();
        private byte[] _hapReadKey = Array.Empty<byte>();
        private ulong _hapWriteCounter;
        private ulong _hapReadCounter;

        public string LocalIp { get; private set; }
        public string ServerIp => _serverIp;
        public int ServerPort => _serverPort;
        private readonly string _clientInstance;

        public bool IsHapEncryptionEnabled => _hapEncryptionEnabled;

        public RtspClient(string serverIp, int serverPort, RSA rsaPublicKey)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
            _client = new TcpClient();
            try
            {
                _client.ConnectAsync(serverIp, serverPort)
                    .WaitAsync(DefaultConnectTimeout)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (TimeoutException ex)
            {
                _client.Dispose();
                throw new TimeoutException(
                    $"Unable to open RTSP connection to {serverIp}:{serverPort} within {DefaultConnectTimeout.TotalSeconds:0}s. Confirm the receiver is online, on the same network, and AirPlay Receiver is enabled.",
                    ex);
            }
            catch (SocketException ex)
            {
                _client.Dispose();
                throw new InvalidOperationException(
                    $"Unable to open RTSP connection to {serverIp}:{serverPort}: {ex.Message} Confirm the receiver is online, on the same network, and AirPlay Receiver is enabled.",
                    ex);
            }

            _client.NoDelay = true;
            _stream = _client.GetStream();
            _cSeq = 0;

            var localEndPoint = (IPEndPoint)_client.Client.LocalEndPoint;
            var localIpRaw = localEndPoint.Address.ToString();
            LocalIp = localIpRaw.StartsWith("::ffff:", StringComparison.Ordinal)
                ? localIpRaw.Substring(7)
                : localIpRaw;

            _clientInstance = GenerateClientInstance();
        }

        public void EnableHapEncryption(byte[] writeKey, byte[] readKey)
        {
            if (writeKey == null || writeKey.Length != 32 || readKey == null || readKey.Length != 32)
            {
                throw new ArgumentException("HAP control keys must be 32 bytes.");
            }

            _hapWriteKey = writeKey.ToArray();
            _hapReadKey = readKey.ToArray();
            _hapWriteCounter = 0;
            _hapReadCounter = 0;
            _hapEncryptionEnabled = true;

            _wireReadBuffer.Clear();
            _plainReadBuffer.Clear();
            Logger.LogMessage("Enabled HAP control channel encryption for RTSP session.", "authentication");
        }

        public async Task<string> SendOptions(string target = "*")
        {
            var headers = GetCommonHeaders();
            headers["Apple-Challenge"] = GenerateAppleChallenge();
            var response = await SendRequestAsync("OPTIONS", target, headers);
            return response.AsText();
        }

        public async Task<string> SendAnnounce(string target, string sdp, string appleChallenge)
        {
            var headers = GetCommonHeaders();
            headers["Content-Type"] = "application/sdp";
            headers["Apple-Challenge"] = appleChallenge;
            var response = await SendRequestAsync("ANNOUNCE", target, headers, Encoding.UTF8.GetBytes(sdp));
            return response.AsText();
        }

        public async Task<string> SendSetup(string target, int controlPort, int timingPort)
        {
            var headers = GetCommonHeaders();
            headers["Transport"] =
                $"RTP/AVP/UDP;unicast;interleaved=0-1;mode=record;control_port={controlPort};timing_port={timingPort}";
            var response = await SendRequestAsync("SETUP", target, headers);
            ParseTransport(response.AsText());
            return response.AsText();
        }

        public async Task<string> SendRecord(string target, string session)
        {
            var headers = GetCommonHeaders();
            headers["Session"] = session;
            headers["Range"] = "npt=0-";
            var response = await SendRequestAsync("RECORD", target, headers);
            return response.AsText();
        }

        public async Task<string> SendTeardown(string target, string session)
        {
            var headers = GetCommonHeaders();
            headers["Session"] = session;
            var response = await SendRequestAsync("TEARDOWN", target, headers);
            return response.AsText();
        }

        public async Task<string> SendSetParameter(string target, string parameter)
        {
            var headers = GetCommonHeaders();
            headers["Content-Type"] = "text/parameters";
            var response = await SendRequestAsync("SET_PARAMETER", target, headers, Encoding.UTF8.GetBytes(parameter));
            return response.AsText();
        }

        public async Task<string> SendAuthSetup(string target, byte[] data)
        {
            var headers = GetCommonHeaders();
            headers["Content-Type"] = "application/octet-stream";
            var response = await SendRequestAsync("POST", target, headers, data, protocol: "HTTP/1.1");
            return response.AsText();
        }

        public async Task<string> SendFlush(string target, string session)
        {
            var headers = GetCommonHeaders();
            headers["Session"] = session;
            headers["RTP-Info"] = "seq=0;rtptime=0";
            var response = await SendRequestAsync("FLUSH", target, headers);
            return response.AsText();
        }

        public Task<RtspResponse> SendCustomRequestAsync(
            string method,
            string target,
            Dictionary<string, string> headers,
            byte[] body = null,
            string protocol = "RTSP/1.0",
            bool includeCommonHeaders = true)
        {
            var merged = includeCommonHeaders ? GetCommonHeaders() : new Dictionary<string, string>();
            if (headers != null)
            {
                foreach (var header in headers)
                {
                    merged[header.Key] = header.Value;
                }
            }

            return SendRequestAsync(method, target, merged, body, protocol);
        }

        private async Task<RtspResponse> SendRequestAsync(
            string method,
            string target,
            Dictionary<string, string> headers,
            byte[] body = null,
            string protocol = "RTSP/1.0")
        {
            try
            {
                var requestBytes = BuildRequestBytes(method, target, protocol, headers, body);
                var outbound = _hapEncryptionEnabled ? EncryptHapControlPayload(requestBytes) : requestBytes;
                LogVerboseWire(
                    $">> {method} {target} {protocol} bodyLen={body?.Length ?? 0} encrypted={_hapEncryptionEnabled}");
                await _stream.WriteAsync(outbound, 0, outbound.Length);
                await _stream.FlushAsync();
                var response = await ReadResponseAsync(ResolveResponseTimeout(target));
                LogVerboseWire(
                    $"<< {response.StatusLine} bodyLen={response.BodyBytes?.Length ?? 0} encrypted={_hapEncryptionEnabled}");
                return response;
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"RTSP send/read failed: {ex.GetType().Name}: {ex.Message}", "authentication");
                return new RtspResponse
                {
                    StatusLine = $"RTSP_ERROR {ex.GetType().Name}: {ex.Message}",
                    Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    BodyBytes = Array.Empty<byte>(),
                };
            }
        }

        private static void LogVerboseWire(string message)
        {
            var value = Environment.GetEnvironmentVariable(VerboseLoggingEnvironmentVariable);
            if (string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogMessage(message, "rtsp-wire");
            }
        }

        private static TimeSpan ResolveResponseTimeout(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return DefaultResponseTimeout;
            }

            return target.Contains("/auth-", StringComparison.OrdinalIgnoreCase) ||
                   target.Contains("/pair-", StringComparison.OrdinalIgnoreCase)
                ? HapResponseTimeout
                : DefaultResponseTimeout;
        }

        private async Task<RtspResponse> ReadResponseAsync(TimeSpan timeout)
        {
            while (true)
            {
                if (TryExtractResponse(_plainReadBuffer, out var response))
                {
                    return response;
                }

                var temp = new byte[8192];
                var read = await _stream.ReadAsync(temp, 0, temp.Length).WaitAsync(timeout);
                if (read <= 0)
                {
                    throw new InvalidOperationException("RTSP connection closed while waiting for response.");
                }

                if (_hapEncryptionEnabled)
                {
                    for (var i = 0; i < read; i++)
                    {
                        _wireReadBuffer.Add(temp[i]);
                    }

                    while (TryDecodeHapControlFrame(out var plaintextFrame))
                    {
                        for (var i = 0; i < plaintextFrame.Length; i++)
                        {
                            _plainReadBuffer.Add(plaintextFrame[i]);
                        }

                        if (TryExtractResponse(_plainReadBuffer, out response))
                        {
                            return response;
                        }
                    }
                }
                else
                {
                    for (var i = 0; i < read; i++)
                    {
                        _plainReadBuffer.Add(temp[i]);
                    }
                }
            }
        }

        private bool TryDecodeHapControlFrame(out byte[] plaintextFrame)
        {
            plaintextFrame = Array.Empty<byte>();
            if (_wireReadBuffer.Count < 2)
            {
                return false;
            }

            var frameLength = _wireReadBuffer[0] | (_wireReadBuffer[1] << 8);
            var totalLength = 2 + frameLength + 16;
            if (_wireReadBuffer.Count < totalLength)
            {
                return false;
            }

            var aad = new byte[2] { _wireReadBuffer[0], _wireReadBuffer[1] };
            var encrypted = _wireReadBuffer.GetRange(2, frameLength + 16).ToArray();
            plaintextFrame = ChaCha20Poly1305Decrypt(_hapReadKey, _hapReadCounter, encrypted, aad);
            _hapReadCounter++;
            _wireReadBuffer.RemoveRange(0, totalLength);
            return true;
        }

        private byte[] EncryptHapControlPayload(byte[] payload)
        {
            const int frameSize = 1024;
            var output = new List<byte>(payload.Length + payload.Length / frameSize * 32);

            var offset = 0;
            while (offset < payload.Length)
            {
                var chunkLength = Math.Min(frameSize, payload.Length - offset);
                var aad = new byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(aad, (ushort)chunkLength);

                var chunk = new byte[chunkLength];
                Buffer.BlockCopy(payload, offset, chunk, 0, chunkLength);
                var encrypted = ChaCha20Poly1305Encrypt(_hapWriteKey, _hapWriteCounter, chunk, aad);
                _hapWriteCounter++;

                output.Add(aad[0]);
                output.Add(aad[1]);
                output.AddRange(encrypted);
                offset += chunkLength;
            }

            return output.ToArray();
        }

        private static byte[] ChaCha20Poly1305Encrypt(byte[] key, ulong counter, byte[] plaintext, byte[] aad)
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, BuildNonce(counter), aad));

            var output = new byte[cipher.GetOutputSize(plaintext.Length)];
            var outLen = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
            outLen += cipher.DoFinal(output, outLen);
            if (outLen == output.Length)
            {
                return output;
            }

            var trimmed = new byte[outLen];
            Buffer.BlockCopy(output, 0, trimmed, 0, outLen);
            return trimmed;
        }

        private static byte[] ChaCha20Poly1305Decrypt(byte[] key, ulong counter, byte[] ciphertextAndTag, byte[] aad)
        {
            var cipher = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, BuildNonce(counter), aad));

            var output = new byte[cipher.GetOutputSize(ciphertextAndTag.Length)];
            var outLen = cipher.ProcessBytes(ciphertextAndTag, 0, ciphertextAndTag.Length, output, 0);
            try
            {
                outLen += cipher.DoFinal(output, outLen);
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
            {
                throw new InvalidOperationException("HAP control frame authentication failed.", ex);
            }

            if (outLen == output.Length)
            {
                return output;
            }

            var trimmed = new byte[outLen];
            Buffer.BlockCopy(output, 0, trimmed, 0, outLen);
            return trimmed;
        }

        private static byte[] BuildNonce(ulong counter)
        {
            var nonce = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
            return nonce;
        }

        private static bool TryExtractResponse(List<byte> buffer, out RtspResponse response)
        {
            response = null;
            if (!TryFindHeaderTerminator(buffer, out var headerEnd, out var delimiterLength))
            {
                return false;
            }

            var headerBytes = buffer.GetRange(0, headerEnd).ToArray();
            var headerText = Encoding.ASCII.GetString(headerBytes);
            var lines = headerText.Contains("\r\n", StringComparison.Ordinal)
                ? headerText.Split(new[] { "\r\n" }, StringSplitOptions.None)
                : headerText.Split('\n');
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
            {
                return false;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in lines.Skip(1))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var separator = line.IndexOf(':');
                if (separator <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separator).Trim();
                var value = line.Substring(separator + 1).Trim();
                headers[key] = value;
            }

            var contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var contentLengthText))
            {
                _ = int.TryParse(contentLengthText, out contentLength);
            }

            var bodyStart = headerEnd + delimiterLength;
            var required = bodyStart + contentLength;
            if (buffer.Count < required)
            {
                return false;
            }

            var body = contentLength > 0 ? buffer.GetRange(bodyStart, contentLength).ToArray() : Array.Empty<byte>();
            buffer.RemoveRange(0, required);

            response = new RtspResponse
            {
                StatusLine = lines[0].TrimEnd('\r'),
                Headers = headers,
                BodyBytes = body,
            };
            return true;
        }

        private static bool TryFindHeaderTerminator(List<byte> buffer, out int headerEnd, out int delimiterLength)
        {
            headerEnd = -1;
            delimiterLength = 0;
            for (var i = 0; i <= buffer.Count - 4; i++)
            {
                if (buffer[i] == '\r' &&
                    buffer[i + 1] == '\n' &&
                    buffer[i + 2] == '\r' &&
                    buffer[i + 3] == '\n')
                {
                    headerEnd = i;
                    delimiterLength = 4;
                    return true;
                }
            }

            for (var i = 0; i <= buffer.Count - 2; i++)
            {
                if (buffer[i] == '\n' && buffer[i + 1] == '\n')
                {
                    headerEnd = i;
                    delimiterLength = 2;
                    return true;
                }
            }

            return false;
        }

        private byte[] BuildRequestBytes(
            string method,
            string target,
            string protocol,
            Dictionary<string, string> headers,
            byte[] body)
        {
            var headersToSend = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
            if (protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                if (!headersToSend.ContainsKey("Host"))
                {
                    headersToSend["Host"] = _serverPort == 80 ? _serverIp : $"{_serverIp}:{_serverPort}";
                }

                if (!headersToSend.ContainsKey("Connection"))
                {
                    headersToSend["Connection"] = "keep-alive";
                }
            }

            var request = new StringBuilder();
            request.Append(method).Append(' ').Append(target).Append(' ').Append(protocol).Append("\r\n");

            foreach (var header in headersToSend)
            {
                request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }

            var bodyLength = body?.Length ?? 0;
            var isBodyMethod =
                method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("PUT", StringComparison.OrdinalIgnoreCase);
            if (!headersToSend.ContainsKey("Content-Length") && (bodyLength > 0 || isBodyMethod))
            {
                request.Append("Content-Length: ").Append(bodyLength).Append("\r\n");
            }

            request.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(request.ToString());
            if (body == null || body.Length == 0)
            {
                return headerBytes;
            }

            var payload = new byte[headerBytes.Length + body.Length];
            Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);
            Buffer.BlockCopy(body, 0, payload, headerBytes.Length, body.Length);
            return payload;
        }

        private Dictionary<string, string> GetCommonHeaders()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CSeq"] = (++_cSeq).ToString(),
                ["User-Agent"] = _userAgent,
                ["Client-Instance"] = _clientInstance,
            };
        }

        private string GenerateClientInstance()
        {
            var rng = new Random();
            var bytes = new byte[64 / 2];
            rng.NextBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static string GenerateAppleChallenge()
        {
            var randomBytes = new byte[16];
            RandomNumberGenerator.Fill(randomBytes);
            return Convert.ToBase64String(randomBytes).TrimEnd('=');
        }

        private void ParseTransport(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return;
            }

            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith("Transport: ", StringComparison.OrdinalIgnoreCase))
                {
                    _transport = line.Substring(11).Trim();
                }
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _client?.Close();
            _client?.Dispose();
        }
    }
}
