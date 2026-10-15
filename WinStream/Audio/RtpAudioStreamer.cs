using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Network;

namespace WinStream.Audio
{
    /// <summary>
    /// Handles RTP audio packet streaming to AirPlay devices.
    /// Sends ALAC-encoded audio frames over UDP using RTP protocol.
    /// </summary>
    public class RtpAudioStreamer : IDisposable
    {
        private readonly UdpClient _audioSocket;
        private readonly IPEndPoint _serverEndpoint;
        private readonly int _framesPerPacket;
        private readonly int _sampleRate;
        private readonly AudioCodec _audioCodec;
        private readonly byte[] _audioAesKey;
        private readonly byte[] _audioAesIv;
        
        private ushort _sequenceNumber;
        private uint _rtpTimestamp;
        private readonly uint _ssrc; // Synchronization source identifier
        
        // RTP header constants
        private const byte RTP_VERSION = 2;
        private const byte RTP_PAYLOAD_TYPE = 96; // Dynamic payload type for ALAC
        
        private bool _isStreaming;
        private CancellationTokenSource _streamingCts;

        public bool IsStreaming => _isStreaming;
        public uint CurrentTimestamp => _rtpTimestamp;
        public ushort CurrentSequence => _sequenceNumber;

        /// <summary>
        /// Creates a new RTP audio streamer
        /// </summary>
        /// <param name="serverIp">AirPlay device IP address</param>
        /// <param name="serverPort">Server audio port from SETUP response</param>
        /// <param name="framesPerPacket">Audio frames per RTP packet (typically 352)</param>
        /// <param name="sampleRate">Audio sample rate (typically 44100)</param>
        public RtpAudioStreamer(
            string serverIp,
            int serverPort,
            int framesPerPacket = 352,
            int sampleRate = 44100,
            AudioCodec audioCodec = AudioCodec.AppleLossless,
            byte[] audioAesKey = null,
            byte[] audioAesIv = null)
        {
            _serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIp), serverPort);
            _audioSocket = new UdpClient();
            _audioSocket.Connect(_serverEndpoint);
            
            _framesPerPacket = framesPerPacket;
            _sampleRate = sampleRate;
            _audioCodec = audioCodec;
            _audioAesKey = IsValidAesMaterial(audioAesKey, audioAesIv) ? (byte[])audioAesKey.Clone() : null;
            _audioAesIv = IsValidAesMaterial(audioAesKey, audioAesIv) ? (byte[])audioAesIv.Clone() : null;
            
            // Initialize RTP state
            _sequenceNumber = (ushort)new Random().Next(0, ushort.MaxValue);
            _rtpTimestamp = (uint)new Random().Next(0, int.MaxValue);
            _ssrc = (uint)new Random().Next();
            
            Logger.LogMessage(
                $"RTP Streamer initialized - Server: {serverIp}:{serverPort}, SSRC: {_ssrc:X8}, Codec: {_audioCodec}, Encryption: {(_audioAesKey != null ? "AES-CBC" : "none")}",
                "audio");
        }

        /// <summary>
        /// Sends an ALAC-encoded audio packet over RTP
        /// </summary>
        /// <param name="alacData">ALAC encoded audio data for one packet (352 frames)</param>
        public async Task SendAudioPacketAsync(byte[] alacData)
        {
            if (alacData == null || alacData.Length == 0)
                return;

            var payload = new byte[4 + alacData.Length];
            payload[0] = 0x00; // Flags
            payload[1] = 0x00; // Reserved
            payload[2] = (byte)(_framesPerPacket >> 8);
            payload[3] = (byte)(_framesPerPacket & 0xFF);
            Buffer.BlockCopy(alacData, 0, payload, 4, alacData.Length);
            payload = EncryptAudioPayloadIfNeeded(payload);

            var rtpPacket = new byte[12 + payload.Length];
            int offset = WriteRtpHeader(rtpPacket, marker: true);
            Buffer.BlockCopy(payload, 0, rtpPacket, offset, payload.Length);

            try
            {
                await _audioSocket.SendAsync(rtpPacket, rtpPacket.Length);
                
                // Update sequence and timestamp for next packet
                _sequenceNumber++;
                _rtpTimestamp += (uint)_framesPerPacket;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending RTP packet: {ex.Message}");
                Logger.LogMessage($"RTP send error: {ex.Message}", "audio");
            }
        }

        /// <summary>
        /// Sends an already-framed compressed audio payload directly after the RTP header.
        /// AirPlay 2 AAC-ELD uses this path; unlike ALAC it must not receive the
        /// four byte AirPlay 1 ALAC frame header.
        /// </summary>
        public async Task SendRawAudioPayloadPacketAsync(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return;
            }

            payload = EncryptAudioPayloadIfNeeded(payload);

            var rtpPacket = new byte[12 + payload.Length];
            var headerOffset = WriteRtpHeader(rtpPacket, marker: true);
            Buffer.BlockCopy(payload, 0, rtpPacket, headerOffset, payload.Length);

            try
            {
                await _audioSocket.SendAsync(rtpPacket, rtpPacket.Length);
                _sequenceNumber++;
                _rtpTimestamp += (uint)_framesPerPacket;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending RTP raw payload packet: {ex.Message}");
                Logger.LogMessage($"RTP raw payload send error: {ex.Message}", "audio");
            }
        }

        /// <summary>
        /// Sends raw PCM audio (will be converted to RTP L16 for legacy receivers).
        /// </summary>
        /// <param name="pcmData">Raw PCM audio data (16-bit stereo)</param>
        public async Task SendPcmPacketAsync(byte[] pcmData)
        {
            if (pcmData == null || pcmData.Length == 0)
            {
                return;
            }

            // RTP L16 expects big-endian signed 16-bit PCM.
            var payload = new byte[pcmData.Length];
            for (int i = 0; i + 1 < pcmData.Length; i += 2)
            {
                payload[i] = pcmData[i + 1];
                payload[i + 1] = pcmData[i];
            }

            payload = EncryptAudioPayloadIfNeeded(payload);

            var rtpPacket = new byte[12 + payload.Length];
            var headerOffset = WriteRtpHeader(rtpPacket, marker: true);
            Buffer.BlockCopy(payload, 0, rtpPacket, headerOffset, payload.Length);

            try
            {
                await _audioSocket.SendAsync(rtpPacket, rtpPacket.Length);
                _sequenceNumber++;
                _rtpTimestamp += (uint)_framesPerPacket;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending RTP PCM packet: {ex.Message}");
                Logger.LogMessage($"RTP PCM send error: {ex.Message}", "audio");
            }
        }

        /// <summary>
        /// Sends silence packets to keep the connection alive
        /// </summary>
        public async Task SendSilenceAsync(int packetCount = 1)
        {
            // Create silent ALAC frame (352 frames * 2 channels * 2 bytes = 1408 bytes of silence)
            var silence = new byte[_framesPerPacket * 2 * 2];
            
            for (int i = 0; i < packetCount; i++)
            {
                if (_audioCodec == AudioCodec.AppleLossless)
                {
                    await SendAudioPacketAsync(silence);
                }
                else if (_audioCodec == AudioCodec.AirPlay2AacEld)
                {
                    await SendRawAudioPayloadPacketAsync(silence);
                }
                else
                {
                    await SendPcmPacketAsync(silence);
                }
                await Task.Delay((_framesPerPacket * 1000) / _sampleRate); // ~8ms per packet
            }
        }

        /// <summary>
        /// Starts continuous streaming from an audio source
        /// </summary>
        public void StartStreaming()
        {
            if (_isStreaming) return;
            
            _isStreaming = true;
            _streamingCts = new CancellationTokenSource();
            Logger.LogMessage("RTP streaming started", "audio");
        }

        /// <summary>
        /// Stops the streaming
        /// </summary>
        public void StopStreaming()
        {
            _isStreaming = false;
            _streamingCts?.Cancel();
            Logger.LogMessage("RTP streaming stopped", "audio");
        }

        /// <summary>
        /// Resyncs the RTP timestamp (called after timing sync)
        /// </summary>
        public void ResyncTimestamp(uint newTimestamp)
        {
            _rtpTimestamp = newTimestamp;
            Logger.LogMessage($"RTP timestamp resynced to {newTimestamp}", "audio");
        }

        public void Dispose()
        {
            StopStreaming();
            _streamingCts?.Dispose();
            _audioSocket?.Close();
            _audioSocket?.Dispose();
        }

        private int WriteRtpHeader(byte[] buffer, bool marker)
        {
            int offset = 0;
            buffer[offset++] = (RTP_VERSION << 6); // 0x80
            buffer[offset++] = (byte)((marker ? 0x80 : 0x00) | RTP_PAYLOAD_TYPE);
            buffer[offset++] = (byte)(_sequenceNumber >> 8);
            buffer[offset++] = (byte)(_sequenceNumber & 0xFF);
            buffer[offset++] = (byte)(_rtpTimestamp >> 24);
            buffer[offset++] = (byte)(_rtpTimestamp >> 16);
            buffer[offset++] = (byte)(_rtpTimestamp >> 8);
            buffer[offset++] = (byte)(_rtpTimestamp & 0xFF);
            buffer[offset++] = (byte)(_ssrc >> 24);
            buffer[offset++] = (byte)(_ssrc >> 16);
            buffer[offset++] = (byte)(_ssrc >> 8);
            buffer[offset++] = (byte)(_ssrc & 0xFF);
            return offset;
        }

        private static bool IsValidAesMaterial(byte[] key, byte[] iv)
            => key?.Length == 16 && iv?.Length == 16;

        private byte[] EncryptAudioPayloadIfNeeded(byte[] payload)
        {
            if (_audioAesKey == null || payload == null || payload.Length == 0)
            {
                return payload;
            }

            var encryptedLength = payload.Length / 16 * 16;
            if (encryptedLength == 0)
            {
                return payload;
            }

            var output = new byte[payload.Length];
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = _audioAesKey;
            aes.IV = _audioAesIv;

            using var encryptor = aes.CreateEncryptor();
            var encrypted = encryptor.TransformFinalBlock(payload, 0, encryptedLength);
            Buffer.BlockCopy(encrypted, 0, output, 0, encrypted.Length);
            Buffer.BlockCopy(payload, encryptedLength, output, encryptedLength, payload.Length - encryptedLength);
            return output;
        }
    }
}
