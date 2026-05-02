using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Network;

namespace WinStream.Audio
{
    /// <summary>
    /// Manages the complete audio streaming session to an AirPlay device.
    /// Orchestrates audio capture, encoding, and RTP streaming with proper timing.
    /// </summary>
    public class AudioSessionManager : IDisposable
    {
        // Core components
        private AudioCaptureService _captureService;
        private Mp3TestAudioSource _mp3TestSource;
        private AlacEncoder _alacEncoder;
        private RtpAudioStreamer _rtpStreamer;
        private TimingService _timingService;
        private ControlService _controlService;

        // Session configuration
        private readonly string _deviceIp;
        private readonly int _serverPort;
        private readonly int _controlPort;
        private readonly int _timingPort;
        private readonly int _audioLatency;
        private readonly AudioCodec _audioCodec;

        // Audio configuration
        private const int SAMPLE_RATE = 44100;
        private const int CHANNELS = 2;
        private const int BITS_PER_SAMPLE = 16;
        private const int FRAMES_PER_PACKET = 352;
        
        // Streaming state
        private bool _isStreaming;
        private CancellationTokenSource _streamingCts;
        private Task _streamingTask;
        private bool _useMp3TestSource;
        private string _audioSourceName = "Unknown";

        // Audio buffer for smoothing
        private readonly ConcurrentQueue<byte[]> _audioBuffer;
        private readonly int _targetBufferSize = 10; // packets to buffer

        // Test source configuration
        private const string TestMp3UrlEnvVar = "WINSTREAM_TEST_MP3_URL";
        private const string DefaultTestMp3Url = "https://www.soundhelix.com/examples/mp3/SoundHelix-Song-1.mp3";

        // Events
        public event EventHandler<string> StatusChanged;
        public event EventHandler<Exception> ErrorOccurred;

        public bool IsStreaming => _isStreaming;
        public string CaptureDeviceName => _audioSourceName;

        /// <summary>
        /// Creates a new audio session manager
        /// </summary>
        /// <param name="deviceIp">AirPlay device IP address</param>
        /// <param name="serverPort">Audio data port from SETUP response</param>
        /// <param name="controlPort">Control port from SETUP response</param>
        /// <param name="timingPort">Timing port from SETUP response</param>
        /// <param name="audioLatency">Audio latency in samples from RECORD response</param>
        public AudioSessionManager(string deviceIp, int serverPort, int controlPort, int timingPort, int audioLatency, AudioCodec audioCodec = AudioCodec.AppleLossless)
        {
            _deviceIp = deviceIp;
            _serverPort = serverPort;
            _controlPort = controlPort;
            _timingPort = timingPort;
            _audioLatency = audioLatency;
            _audioCodec = audioCodec;

            _audioBuffer = new ConcurrentQueue<byte[]>();

            Logger.LogMessage($"Audio session manager created for {deviceIp}", "session");
            Logger.LogMessage($"  Server port: {serverPort}, Control port: {controlPort}, Timing port: {timingPort}", "session");
            Logger.LogMessage($"  Audio latency: {audioLatency} samples ({(audioLatency * 1000.0 / SAMPLE_RATE):F1}ms)", "session");
            Logger.LogMessage($"  Audio codec: {_audioCodec}", "session");
        }

        /// <summary>
        /// Initializes all audio components
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                StatusChanged?.Invoke(this, "Initializing audio components...");

                // Create ALAC encoder
                if (_audioCodec == AudioCodec.AppleLossless)
                {
                    _alacEncoder = new AlacEncoder(FRAMES_PER_PACKET, SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);
                    Logger.LogMessage("ALAC encoder created", "session");
                }
                else
                {
                    Logger.LogMessage("Using L16 mode - ALAC encoder disabled", "session");
                }

                // Create RTP streamer
                _rtpStreamer = new RtpAudioStreamer(_deviceIp, _serverPort, FRAMES_PER_PACKET, SAMPLE_RATE, _audioCodec);
                Logger.LogMessage("RTP streamer created", "session");

                // Create timing service
                _timingService = new TimingService(_deviceIp, _timingPort);
                Logger.LogMessage("Timing service created", "session");

                // Create control service
                _controlService = new ControlService(_deviceIp, _controlPort);
                Logger.LogMessage("Control service created", "session");

                // MP3 test mode can be forced with an environment variable for transport testing.
                var configuredTestSource = Environment.GetEnvironmentVariable(TestMp3UrlEnvVar);
                if (!string.IsNullOrWhiteSpace(configuredTestSource))
                {
                    if (!await TryInitializeMp3TestSourceAsync(configuredTestSource))
                    {
                        StatusChanged?.Invoke(this, "Failed to initialize MP3 test source");
                        return false;
                    }
                }
                else
                {
                    // Create and initialize audio capture
                    _captureService = new AudioCaptureService(SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE, FRAMES_PER_PACKET);
                    
                    if (!await _captureService.InitializeAsync() || !await _captureService.CreateLoopbackCaptureAsync())
                    {
                        Logger.LogMessage("Audio capture init failed; falling back to MP3 test source", "session");
                        if (!await TryInitializeMp3TestSourceAsync(DefaultTestMp3Url))
                        {
                            StatusChanged?.Invoke(this, "Failed to initialize audio capture and MP3 fallback");
                            return false;
                        }
                    }
                    else
                    {
                        _captureService.AudioDataAvailable += OnAudioDataAvailable;
                        _audioSourceName = _captureService.CaptureDeviceName;
                        _useMp3TestSource = false;
                    }
                }

                StatusChanged?.Invoke(this, "Audio components initialized");
                Logger.LogMessage("All audio components initialized successfully", "session");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                ErrorOccurred?.Invoke(this, ex);
                return false;
            }
        }

        /// <summary>
        /// Starts the streaming session
        /// </summary>
        public async Task StartStreamingAsync()
        {
            if (_isStreaming)
            {
                Logger.LogMessage("Already streaming", "session");
                return;
            }

            try
            {
                _isStreaming = true;
                _streamingCts = new CancellationTokenSource();

                // Start timing service first
                _timingService.Start();
                await Task.Delay(100); // Let timing sync establish

                // Start control service
                _controlService.Start();

                // Send initial timing sync
                await _timingService.SendTimingRequest();

                // Pre-buffer with silence to match audio latency
                StatusChanged?.Invoke(this, "Pre-buffering...");
                int prebufferPackets = _audioLatency / FRAMES_PER_PACKET;
                Logger.LogMessage($"Pre-buffering {prebufferPackets} silence packets", "session");
                
                await _rtpStreamer.SendSilenceAsync(prebufferPackets);

                // Start active audio source
                if (_useMp3TestSource)
                {
                    _mp3TestSource.Start();
                }
                else
                {
                    _captureService.StartCapture();
                }

                // Start streaming task
                _streamingTask = Task.Run(() => StreamingLoop(_streamingCts.Token));

                StatusChanged?.Invoke(this, "Streaming started");
                Logger.LogMessage("Streaming session started", "session");
            }
            catch (Exception ex)
            {
                _isStreaming = false;
                Logger.LogException(ex);
                ErrorOccurred?.Invoke(this, ex);
                throw;
            }
        }

        /// <summary>
        /// Stops the streaming session
        /// </summary>
        public async Task StopStreamingAsync()
        {
            if (!_isStreaming) return;

            _isStreaming = false;
            _streamingCts?.Cancel();

            try
            {
                // Wait for streaming task to complete
                if (_streamingTask != null)
                {
                    await Task.WhenAny(_streamingTask, Task.Delay(2000));
                }
            }
            catch { }

            // Stop all services
            if (_useMp3TestSource)
            {
                _mp3TestSource?.Stop();
            }
            else
            {
                _captureService?.StopCapture();
            }
            _controlService?.Stop();
            _timingService?.Stop();
            _rtpStreamer?.StopStreaming();

            // Clear buffer
            while (_audioBuffer.TryDequeue(out _)) { }

            StatusChanged?.Invoke(this, "Streaming stopped");
            Logger.LogMessage("Streaming session stopped", "session");
        }

        /// <summary>
        /// Handles incoming audio data from capture
        /// </summary>
        private void OnAudioDataAvailable(object sender, byte[] audioData)
        {
            if (!_isStreaming) return;

            // Buffer the audio data
            _audioBuffer.Enqueue(audioData);

            // Prevent buffer from growing too large
            while (_audioBuffer.Count > _targetBufferSize * 2)
            {
                _audioBuffer.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Main streaming loop - processes buffered audio and sends to device
        /// </summary>
        private async Task StreamingLoop(CancellationToken cancellationToken)
        {
            var packetBuffer = new byte[FRAMES_PER_PACKET * CHANNELS * (BITS_PER_SAMPLE / 8)];
            int packetBufferOffset = 0;
            int packetsSent = 0;

            var stopwatch = Stopwatch.StartNew();
            double expectedTime = 0;

            while (!cancellationToken.IsCancellationRequested && _isStreaming)
            {
                try
                {
                    // Try to get audio data from buffer
                    if (_audioBuffer.TryDequeue(out var audioData))
                    {
                        // Consume the full captured buffer; dropping the tail causes near-silent output.
                        int sourceOffset = 0;
                        while (sourceOffset < audioData.Length)
                        {
                            int bytesToCopy = Math.Min(audioData.Length - sourceOffset, packetBuffer.Length - packetBufferOffset);
                            Buffer.BlockCopy(audioData, sourceOffset, packetBuffer, packetBufferOffset, bytesToCopy);
                            packetBufferOffset += bytesToCopy;
                            sourceOffset += bytesToCopy;

                            if (packetBufferOffset < packetBuffer.Length)
                            {
                                continue;
                            }

                            if (_audioCodec == AudioCodec.AppleLossless)
                            {
                                // Encode to ALAC
                                var alacData = _alacEncoder.Encode(packetBuffer);
                                await _rtpStreamer.SendAudioPacketAsync(alacData);
                            }
                            else
                            {
                                await _rtpStreamer.SendPcmPacketAsync(packetBuffer);
                            }
                            packetsSent++;

                            // Update control service with current timestamp
                            _controlService.UpdateTimestamp(_rtpStreamer.CurrentTimestamp);

                            // Timing control - maintain proper packet rate
                            expectedTime += (FRAMES_PER_PACKET * 1000.0) / SAMPLE_RATE; // ~8ms per packet
                            var actualTime = stopwatch.Elapsed.TotalMilliseconds;
                            var sleepTime = expectedTime - actualTime;
                            
                            if (sleepTime > 1)
                            {
                                await Task.Delay((int)sleepTime, cancellationToken);
                            }
                            else if (sleepTime < -50) // We're falling behind
                            {
                                // Reset timing
                                expectedTime = actualTime;
                                Logger.LogMessage("Audio timing reset - buffer underrun", "session");
                            }

                            // Reset packet buffer
                            packetBufferOffset = 0;

                            // Periodic logging
                            if (packetsSent % 500 == 0)
                            {
                                Logger.LogMessage($"Streamed {packetsSent} packets ({packetsSent * FRAMES_PER_PACKET / SAMPLE_RATE:F1}s)", "session");
                            }
                        }
                    }
                    else
                    {
                        // No data available, send silence to keep stream alive
                        if (packetBufferOffset == 0)
                        {
                            await _rtpStreamer.SendSilenceAsync(1);
                            expectedTime += (FRAMES_PER_PACKET * 1000.0) / SAMPLE_RATE;
                        }
                        await Task.Delay(1, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Streaming loop error: {ex.Message}");
                    Logger.LogMessage($"Streaming error: {ex.Message}", "session");
                    ErrorOccurred?.Invoke(this, ex);
                    
                    // Brief pause before retrying
                    await Task.Delay(100, cancellationToken);
                }
            }

            Logger.LogMessage($"Streaming loop ended - sent {packetsSent} packets", "session");
        }

        /// <summary>
        /// Sends a test tone to verify audio is working
        /// </summary>
        public async Task SendTestToneAsync(int durationMs = 1000, int frequencyHz = 440)
        {
            Logger.LogMessage($"Sending {frequencyHz}Hz test tone for {durationMs}ms", "session");

            int totalSamples = (SAMPLE_RATE * durationMs) / 1000;
            int totalPackets = totalSamples / FRAMES_PER_PACKET;
            
            for (int packet = 0; packet < totalPackets; packet++)
            {
                var pcmData = new byte[FRAMES_PER_PACKET * CHANNELS * (BITS_PER_SAMPLE / 8)];
                
                for (int frame = 0; frame < FRAMES_PER_PACKET; frame++)
                {
                    int sampleIndex = packet * FRAMES_PER_PACKET + frame;
                    double t = (double)sampleIndex / SAMPLE_RATE;
                    short sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * t) * 16000);
                    
                    int offset = frame * 4; // 2 channels * 2 bytes
                    // Left channel
                    pcmData[offset] = (byte)(sample & 0xFF);
                    pcmData[offset + 1] = (byte)((sample >> 8) & 0xFF);
                    // Right channel
                    pcmData[offset + 2] = (byte)(sample & 0xFF);
                    pcmData[offset + 3] = (byte)((sample >> 8) & 0xFF);
                }

                if (_audioCodec == AudioCodec.AppleLossless)
                {
                    var alacData = _alacEncoder.Encode(pcmData);
                    await _rtpStreamer.SendAudioPacketAsync(alacData);
                }
                else
                {
                    await _rtpStreamer.SendPcmPacketAsync(pcmData);
                }
                
                _controlService.UpdateTimestamp(_rtpStreamer.CurrentTimestamp);
                
                await Task.Delay((FRAMES_PER_PACKET * 1000) / SAMPLE_RATE);
            }

            Logger.LogMessage("Test tone complete", "session");
        }

        private async Task<bool> TryInitializeMp3TestSourceAsync(string source)
        {
            _mp3TestSource = new Mp3TestAudioSource(SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE, FRAMES_PER_PACKET);
            if (!await _mp3TestSource.InitializeAsync(source))
            {
                return false;
            }

            _mp3TestSource.AudioDataAvailable += OnAudioDataAvailable;
            _audioSourceName = $"MP3: {source}";
            _useMp3TestSource = true;
            Logger.LogMessage($"Using MP3 test source: {source}", "session");
            return true;
        }

        public void Dispose()
        {
            StopStreamingAsync().Wait(2000);
            
            _streamingCts?.Dispose();
            _captureService?.Dispose();
            _mp3TestSource?.Dispose();
            _alacEncoder?.Dispose();
            _rtpStreamer?.Dispose();
            _timingService?.Dispose();
            _controlService?.Dispose();
        }
    }
}
