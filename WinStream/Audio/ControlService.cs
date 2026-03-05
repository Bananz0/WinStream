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
    /// Handles RTCP control protocol for AirPlay streaming.
    /// Sends sync packets to keep audio synchronized with the device.
    /// </summary>
    public class ControlService : IDisposable
    {
        private readonly UdpClient _controlSocket;
        private readonly IPEndPoint _deviceEndpoint;
        private readonly int _localPort;
        
        private CancellationTokenSource _syncCts;
        private Task _syncTask;
        
        // Sync state
        private uint _currentRtpTimestamp;
        private uint _currentNtpTimestampHigh;
        private uint _currentNtpTimestampLow;
        
        // RTCP constants
        private const byte RTCP_VERSION = 2;
        private const byte RTCP_SENDER_REPORT = 200; // SR
        private const byte RTCP_SYNC = 0x54; // AirPlay sync packet type
        
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Creates a new control service
        /// </summary>
        /// <param name="deviceIp">AirPlay device IP</param>
        /// <param name="deviceControlPort">Device's control port from SETUP response</param>
        /// <param name="localPort">Local port to bind (same as specified in SETUP)</param>
        public ControlService(string deviceIp, int deviceControlPort, int localPort = 6001)
        {
            _localPort = localPort;
            _deviceEndpoint = new IPEndPoint(IPAddress.Parse(deviceIp), deviceControlPort);
            
            // Bind to local control port
            _controlSocket = new UdpClient(_localPort);
            
            Logger.LogMessage($"Control service initialized - Local port: {_localPort}, Device: {deviceIp}:{deviceControlPort}", "control");
        }

        /// <summary>
        /// Starts the sync packet sender
        /// </summary>
        /// <param name="syncIntervalMs">Interval between sync packets in milliseconds</param>
        public void Start(int syncIntervalMs = 1000)
        {
            if (IsRunning) return;
            
            IsRunning = true;
            _syncCts = new CancellationTokenSource();
            _syncTask = Task.Run(() => SendSyncPacketsLoop(syncIntervalMs, _syncCts.Token));
            
            Logger.LogMessage("Control service started", "control");
        }

        /// <summary>
        /// Stops the control service
        /// </summary>
        public void Stop()
        {
            IsRunning = false;
            _syncCts?.Cancel();
            
            try
            {
                _syncTask?.Wait(1000);
            }
            catch { }
            
            Logger.LogMessage("Control service stopped", "control");
        }

        /// <summary>
        /// Periodically sends sync packets
        /// </summary>
        private async Task SendSyncPacketsLoop(int intervalMs, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsRunning)
            {
                try
                {
                    await SendSyncPacket();
                    await Task.Delay(intervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Control sync error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Updates the current RTP timestamp for sync packets
        /// </summary>
        public void UpdateTimestamp(uint rtpTimestamp)
        {
            _currentRtpTimestamp = rtpTimestamp;
            
            // Calculate NTP timestamp
            var now = DateTime.UtcNow;
            var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var seconds = (uint)(now - epoch).TotalSeconds;
            var fraction = (uint)((now - epoch).Milliseconds * (uint.MaxValue / 1000.0));
            
            _currentNtpTimestampHigh = seconds;
            _currentNtpTimestampLow = fraction;
        }

        /// <summary>
        /// Sends an AirPlay sync packet to keep audio in sync
        /// </summary>
        public async Task SendSyncPacket()
        {
            // AirPlay sync packet format:
            // Byte 0: 0x80 | extension flag
            // Byte 1: 0xD4 (sync packet) or 0x54 (first sync)
            // Bytes 2-3: Sequence (0)
            // Bytes 4-7: Current RTP timestamp (what's being played now)
            // Bytes 8-15: NTP timestamp
            // Bytes 16-19: Next RTP timestamp (next packet timestamp)
            
            var packet = new byte[20];
            int offset = 0;
            
            // Header
            packet[offset++] = 0x80;
            packet[offset++] = 0xD4; // Sync packet type
            
            // Sequence (zero for sync)
            packet[offset++] = 0x00;
            packet[offset++] = 0x00;
            
            // Current RTP timestamp (big-endian)
            packet[offset++] = (byte)(_currentRtpTimestamp >> 24);
            packet[offset++] = (byte)(_currentRtpTimestamp >> 16);
            packet[offset++] = (byte)(_currentRtpTimestamp >> 8);
            packet[offset++] = (byte)(_currentRtpTimestamp & 0xFF);
            
            // NTP timestamp high (seconds)
            packet[offset++] = (byte)(_currentNtpTimestampHigh >> 24);
            packet[offset++] = (byte)(_currentNtpTimestampHigh >> 16);
            packet[offset++] = (byte)(_currentNtpTimestampHigh >> 8);
            packet[offset++] = (byte)(_currentNtpTimestampHigh & 0xFF);
            
            // NTP timestamp low (fraction)
            packet[offset++] = (byte)(_currentNtpTimestampLow >> 24);
            packet[offset++] = (byte)(_currentNtpTimestampLow >> 16);
            packet[offset++] = (byte)(_currentNtpTimestampLow >> 8);
            packet[offset++] = (byte)(_currentNtpTimestampLow & 0xFF);
            
            // Next RTP timestamp (current + frames per packet)
            uint nextTimestamp = _currentRtpTimestamp + 352; // 352 frames per packet
            packet[offset++] = (byte)(nextTimestamp >> 24);
            packet[offset++] = (byte)(nextTimestamp >> 16);
            packet[offset++] = (byte)(nextTimestamp >> 8);
            packet[offset++] = (byte)(nextTimestamp & 0xFF);
            
            try
            {
                await _controlSocket.SendAsync(packet, packet.Length, _deviceEndpoint);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending sync packet: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a resync request (used when audio buffer underruns)
        /// </summary>
        public async Task SendResyncRequest(uint rtpTimestamp, ushort sequenceNumber)
        {
            // Resync packet tells the receiver to resynchronize its buffer
            var packet = new byte[8];
            
            packet[0] = 0x80;
            packet[1] = 0xD5; // Resync request
            
            // Sequence number
            packet[2] = (byte)(sequenceNumber >> 8);
            packet[3] = (byte)(sequenceNumber & 0xFF);
            
            // RTP timestamp
            packet[4] = (byte)(rtpTimestamp >> 24);
            packet[5] = (byte)(rtpTimestamp >> 16);
            packet[6] = (byte)(rtpTimestamp >> 8);
            packet[7] = (byte)(rtpTimestamp & 0xFF);
            
            try
            {
                await _controlSocket.SendAsync(packet, packet.Length, _deviceEndpoint);
                Logger.LogMessage($"Sent resync request: seq={sequenceNumber}, timestamp={rtpTimestamp}", "control");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending resync request: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
            _syncCts?.Dispose();
            _controlSocket?.Close();
            _controlSocket?.Dispose();
        }
    }
}
