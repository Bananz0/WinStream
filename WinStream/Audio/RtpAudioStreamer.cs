using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
        public RtpAudioStreamer(string serverIp, int serverPort, int framesPerPacket = 352, int sampleRate = 44100)
        {
            _serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIp), serverPort);
            _audioSocket = new UdpClient();
            _audioSocket.Connect(_serverEndpoint);
            
            _framesPerPacket = framesPerPacket;
            _sampleRate = sampleRate;
            
            // Initialize RTP state
            _sequenceNumber = (ushort)new Random().Next(0, ushort.MaxValue);
            _rtpTimestamp = (uint)new Random().Next(0, int.MaxValue);
            _ssrc = (uint)new Random().Next();
            
            Logger.LogMessage($"RTP Streamer initialized - Server: {serverIp}:{serverPort}, SSRC: {_ssrc:X8}", "audio");
        }

        /// <summary>
        /// Sends an ALAC-encoded audio packet over RTP
        /// </summary>
        /// <param name="alacData">ALAC encoded audio data for one packet (352 frames)</param>
        public async Task SendAudioPacketAsync(byte[] alacData)
        {
            if (alacData == null || alacData.Length == 0)
                return;

            // Build RTP packet
            // RTP Header: 12 bytes + optional ALAC header (4 bytes) + payload
            var rtpPacket = new byte[12 + 4 + alacData.Length];
            int offset = 0;

            // Byte 0: V=2, P=0, X=0, CC=0
            rtpPacket[offset++] = (RTP_VERSION << 6); // 0x80

            // Byte 1: M=1 (marker), PT=96
            rtpPacket[offset++] = (byte)(0x80 | RTP_PAYLOAD_TYPE); // 0xE0

            // Bytes 2-3: Sequence number (big-endian)
            rtpPacket[offset++] = (byte)(_sequenceNumber >> 8);
            rtpPacket[offset++] = (byte)(_sequenceNumber & 0xFF);

            // Bytes 4-7: Timestamp (big-endian)
            rtpPacket[offset++] = (byte)(_rtpTimestamp >> 24);
            rtpPacket[offset++] = (byte)(_rtpTimestamp >> 16);
            rtpPacket[offset++] = (byte)(_rtpTimestamp >> 8);
            rtpPacket[offset++] = (byte)(_rtpTimestamp & 0xFF);

            // Bytes 8-11: SSRC (big-endian)
            rtpPacket[offset++] = (byte)(_ssrc >> 24);
            rtpPacket[offset++] = (byte)(_ssrc >> 16);
            rtpPacket[offset++] = (byte)(_ssrc >> 8);
            rtpPacket[offset++] = (byte)(_ssrc & 0xFF);

            // ALAC-specific header (4 bytes) - describes the audio packet
            // This is the "ALAC specific config" required by AirPlay
            rtpPacket[offset++] = 0x00; // Flags
            rtpPacket[offset++] = 0x00; // Reserved
            rtpPacket[offset++] = (byte)(_framesPerPacket >> 8); // Frames per packet (high)
            rtpPacket[offset++] = (byte)(_framesPerPacket & 0xFF); // Frames per packet (low)

            // Copy ALAC payload
            Buffer.BlockCopy(alacData, 0, rtpPacket, offset, alacData.Length);

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
        /// Sends raw PCM audio (will be converted to simple format for testing)
        /// For production, use SendAudioPacketAsync with proper ALAC encoding
        /// </summary>
        /// <param name="pcmData">Raw PCM audio data (16-bit stereo)</param>
        public async Task SendPcmPacketAsync(byte[] pcmData)
        {
            // For now, send PCM directly - some AirPlay receivers accept this
            // In production, this should be ALAC encoded
            await SendAudioPacketAsync(pcmData);
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
                await SendAudioPacketAsync(silence);
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
    }
}
