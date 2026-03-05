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
    /// Handles NTP-like timing synchronization with AirPlay devices.
    /// AirPlay uses a custom timing protocol on the timing port to synchronize
    /// clocks between the sender and receiver for proper audio playback.
    /// </summary>
    public class TimingService : IDisposable
    {
        private readonly UdpClient _timingSocket;
        private readonly IPEndPoint _deviceEndpoint;
        private readonly int _localPort;
        
        private CancellationTokenSource _listenerCts;
        private Task _listenerTask;
        
        // Timing state
        private long _referenceTime; // Our reference timestamp
        private long _deviceOffset;  // Offset between our clock and device clock
        private readonly Stopwatch _stopwatch;
        
        // NTP-like timing packet structure for AirPlay
        private const int TIMING_PACKET_SIZE = 32;
        private const byte TIMING_REQUEST = 0x52;  // 'R' for request
        private const byte TIMING_RESPONSE = 0x53; // 'S' for response
        
        public bool IsRunning { get; private set; }
        public long DeviceOffset => _deviceOffset;

        /// <summary>
        /// Creates a new timing service
        /// </summary>
        /// <param name="deviceIp">AirPlay device IP</param>
        /// <param name="deviceTimingPort">Device's timing port from SETUP response</param>
        /// <param name="localPort">Local port to bind for timing (same as specified in SETUP)</param>
        public TimingService(string deviceIp, int deviceTimingPort, int localPort = 6002)
        {
            _localPort = localPort;
            _deviceEndpoint = new IPEndPoint(IPAddress.Parse(deviceIp), deviceTimingPort);
            
            // Bind to local timing port
            _timingSocket = new UdpClient(_localPort);
            
            _stopwatch = Stopwatch.StartNew();
            _referenceTime = GetCurrentNtpTimestamp();
            
            Logger.LogMessage($"Timing service initialized - Local port: {_localPort}, Device: {deviceIp}:{deviceTimingPort}", "timing");
        }

        /// <summary>
        /// Starts listening for timing requests from the device
        /// </summary>
        public void Start()
        {
            if (IsRunning) return;
            
            IsRunning = true;
            _listenerCts = new CancellationTokenSource();
            _listenerTask = Task.Run(() => ListenForTimingRequests(_listenerCts.Token));
            
            Logger.LogMessage("Timing service started", "timing");
        }

        /// <summary>
        /// Stops the timing service
        /// </summary>
        public void Stop()
        {
            IsRunning = false;
            _listenerCts?.Cancel();
            
            try
            {
                _listenerTask?.Wait(1000);
            }
            catch { }
            
            Logger.LogMessage("Timing service stopped", "timing");
        }

        /// <summary>
        /// Listens for timing requests and responds with timing data
        /// </summary>
        private async Task ListenForTimingRequests(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var receiveTask = _timingSocket.ReceiveAsync();
                    var timeoutTask = Task.Delay(1000, cancellationToken);
                    
                    var completedTask = await Task.WhenAny(receiveTask, timeoutTask);
                    
                    if (completedTask == receiveTask)
                    {
                        var result = await receiveTask;
                        await ProcessTimingPacket(result.Buffer, result.RemoteEndPoint);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Timing receive error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Processes incoming timing packet and sends response
        /// </summary>
        private async Task ProcessTimingPacket(byte[] data, IPEndPoint sender)
        {
            if (data.Length < 8) return;

            // AirPlay timing packet format:
            // Bytes 0-1: Header (0x80, 0xD2 or 0x80, 0xD3)
            // Bytes 2-3: Sequence number
            // Bytes 4-7: Zero padding
            // Bytes 8-15: Reference timestamp (NTP format)
            // Bytes 16-23: Receive timestamp (filled by receiver)
            // Bytes 24-31: Transmit timestamp (filled by sender on response)

            var header = data[1];
            
            // Check if this is a timing request (0xD2 = request, 0xD3 = response)
            if ((header & 0x7F) == 0x52) // Request
            {
                await SendTimingResponse(data, sender);
            }
            else if ((header & 0x7F) == 0x53) // Response - calculate offset
            {
                CalculateClockOffset(data);
            }
        }

        /// <summary>
        /// Sends a timing response packet
        /// </summary>
        private async Task SendTimingResponse(byte[] request, IPEndPoint sender)
        {
            var response = new byte[TIMING_PACKET_SIZE];
            
            // Copy header and sequence from request
            response[0] = 0x80;
            response[1] = 0xD3; // Response type
            response[2] = request[2]; // Sequence high
            response[3] = request[3]; // Sequence low
            
            // Zero padding
            response[4] = 0;
            response[5] = 0;
            response[6] = 0;
            response[7] = 0;
            
            // Copy origin timestamp from request (bytes 8-15 from request -> 8-15 in response)
            if (request.Length >= 16)
            {
                Buffer.BlockCopy(request, 8, response, 8, 8);
            }
            
            // Set receive timestamp (when we received the request) - bytes 16-23
            var receiveTime = GetCurrentNtpTimestamp();
            WriteNtpTimestamp(response, 16, receiveTime);
            
            // Set transmit timestamp (now) - bytes 24-31
            var transmitTime = GetCurrentNtpTimestamp();
            WriteNtpTimestamp(response, 24, transmitTime);
            
            try
            {
                await _timingSocket.SendAsync(response, response.Length, sender);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending timing response: {ex.Message}");
            }
        }

        /// <summary>
        /// Sends a timing request to the device to measure clock offset
        /// </summary>
        public async Task SendTimingRequest()
        {
            var request = new byte[TIMING_PACKET_SIZE];
            
            request[0] = 0x80;
            request[1] = 0xD2; // Request type
            
            // Sequence number
            var seq = (ushort)(DateTime.UtcNow.Ticks & 0xFFFF);
            request[2] = (byte)(seq >> 8);
            request[3] = (byte)(seq & 0xFF);
            
            // Origin timestamp - bytes 8-15
            var originTime = GetCurrentNtpTimestamp();
            WriteNtpTimestamp(request, 8, originTime);
            
            try
            {
                await _timingSocket.SendAsync(request, request.Length, _deviceEndpoint);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error sending timing request: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculates clock offset from a timing response
        /// </summary>
        private void CalculateClockOffset(byte[] response)
        {
            if (response.Length < 32) return;
            
            // T1 = Origin timestamp (when we sent request)
            // T2 = Receive timestamp (when device received request)
            // T3 = Transmit timestamp (when device sent response)
            // T4 = Now (when we received response)
            
            var t1 = ReadNtpTimestamp(response, 8);
            var t2 = ReadNtpTimestamp(response, 16);
            var t3 = ReadNtpTimestamp(response, 24);
            var t4 = GetCurrentNtpTimestamp();
            
            // Offset = ((T2 - T1) + (T3 - T4)) / 2
            _deviceOffset = ((t2 - t1) + (t3 - t4)) / 2;
            
            Debug.WriteLine($"Timing offset calculated: {_deviceOffset}");
        }

        /// <summary>
        /// Gets current time as NTP timestamp (microseconds since epoch)
        /// </summary>
        public long GetCurrentNtpTimestamp()
        {
            // NTP epoch is January 1, 1900
            // We use microseconds for better precision
            var now = DateTime.UtcNow;
            var epoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var elapsed = now - epoch;
            return (long)(elapsed.TotalMilliseconds * 1000); // microseconds
        }

        /// <summary>
        /// Converts RTP timestamp to device-synchronized timestamp
        /// </summary>
        public uint GetSynchronizedRtpTimestamp(uint rtpTimestamp)
        {
            return (uint)(rtpTimestamp + _deviceOffset);
        }

        private void WriteNtpTimestamp(byte[] buffer, int offset, long timestamp)
        {
            // Write as 64-bit big-endian
            for (int i = 7; i >= 0; i--)
            {
                buffer[offset + (7 - i)] = (byte)(timestamp >> (i * 8));
            }
        }

        private long ReadNtpTimestamp(byte[] buffer, int offset)
        {
            long value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | buffer[offset + i];
            }
            return value;
        }

        public void Dispose()
        {
            Stop();
            _listenerCts?.Dispose();
            _timingSocket?.Close();
            _timingSocket?.Dispose();
        }
    }
}
