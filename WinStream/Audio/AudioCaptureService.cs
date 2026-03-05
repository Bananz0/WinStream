using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinStream.Network;

namespace WinStream.Audio
{
    /// <summary>
    /// Captures system audio using WASAPI loopback via NAudio.
    /// Captures whatever is playing on the selected render device (default output or VB-Cable).
    /// Delivers 352-frame 16-bit stereo PCM packets at 44100 Hz via AudioDataAvailable events.
    /// </summary>
    public class AudioCaptureService : IDisposable
    {
        private WasapiLoopbackCapture _capture;
        private BufferedWaveProvider _buffer;
        private IWaveProvider _convertedProvider;

        private Thread _processingThread;
        private CancellationTokenSource _captureCts;
        private bool _isCapturing;

        private const int TARGET_SAMPLE_RATE = 44100;
        private const int TARGET_CHANNELS = 2;
        private const int TARGET_BITS = 16;

        private readonly int _framesPerPacket;

        public event EventHandler<byte[]> AudioDataAvailable;
        public event EventHandler<bool> CaptureStatusChanged;

        public bool IsCapturing => _isCapturing;

        /// <summary>
        /// Friendly name of the device being captured (set after InitializeAsync).
        /// </summary>
        public string CaptureDeviceName { get; private set; } = "Not initialized";

        public AudioCaptureService(int sampleRate = 44100, int channels = 2, int bitsPerSample = 16, int framesPerPacket = 352)
        {
            _framesPerPacket = framesPerPacket;
            Logger.LogMessage($"Audio capture service created - target: {TARGET_SAMPLE_RATE}Hz, {TARGET_CHANNELS}ch, {TARGET_BITS}bit", "audio");
        }

        /// <summary>
        /// Finds best capture device and creates the WasapiLoopbackCapture.
        /// Prefers virtual audio cables (VB-Cable, VoiceMeeter) over the default render device.
        /// </summary>
        public Task<bool> InitializeAsync()
        {
            try
            {
                var device = FindBestCaptureDevice();
                CaptureDeviceName = device?.FriendlyName ?? "Default System Audio";

                _capture = device != null
                    ? new WasapiLoopbackCapture(device)
                    : new WasapiLoopbackCapture();

                Logger.LogMessage($"Capture device: {CaptureDeviceName}", "audio");
                Logger.LogMessage($"Capture native format: {_capture.WaveFormat}", "audio");

                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                Logger.LogMessage($"Failed to initialize capture: {ex.Message}", "audio");
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Builds the format conversion chain from the capture device's native format
        /// to 44100 Hz / stereo / 16-bit PCM required by ALAC encoding.
        /// </summary>
        public Task<bool> CreateLoopbackCaptureAsync()
        {
            try
            {
                if (_capture == null)
                {
                    Logger.LogMessage("Capture not initialized", "audio");
                    return Task.FromResult(false);
                }

                var captureFormat = _capture.WaveFormat;

                _buffer = new BufferedWaveProvider(captureFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(3),
                    DiscardOnBufferOverflow = true
                };

                // Step 1: To sample provider (handles IEEE float and PCM)
                ISampleProvider sampleProvider = captureFormat.Encoding == WaveFormatEncoding.IeeeFloat
                    ? new WaveToSampleProvider(_buffer)
                    : _buffer.ToSampleProvider();

                // Step 2: Mix down to stereo if needed (e.g. 5.1 surround)
                if (captureFormat.Channels != TARGET_CHANNELS)
                {
                    Logger.LogMessage($"Channel conversion: {captureFormat.Channels}ch → {TARGET_CHANNELS}ch", "audio");
                    sampleProvider = new MultiplexingSampleProvider(new[] { sampleProvider }, TARGET_CHANNELS);
                }

                // Step 3: Resample to 44100 if needed (e.g. device running at 48000)
                if (captureFormat.SampleRate != TARGET_SAMPLE_RATE)
                {
                    Logger.LogMessage($"Resampling: {captureFormat.SampleRate}Hz → {TARGET_SAMPLE_RATE}Hz", "audio");
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, TARGET_SAMPLE_RATE);
                }

                // Step 4: Convert float samples to 16-bit PCM
                _convertedProvider = new SampleToWaveProvider16(sampleProvider);

                // Wire up data callback
                _capture.DataAvailable += (_, e) => _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

                Logger.LogMessage("Loopback capture chain configured", "audio");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                Logger.LogMessage($"Failed to create loopback capture: {ex.Message}", "audio");
                return Task.FromResult(false);
            }
        }

        public void StartCapture()
        {
            if (_isCapturing || _capture == null) return;

            _isCapturing = true;
            _captureCts = new CancellationTokenSource();

            _capture.StartRecording();

            _processingThread = new Thread(() => ProcessingLoop(_captureCts.Token))
            {
                IsBackground = true,
                Name = "AudioCaptureProcessor"
            };
            _processingThread.Start();

            CaptureStatusChanged?.Invoke(this, true);
            Logger.LogMessage("Audio capture started", "audio");
        }

        public void StopCapture()
        {
            if (!_isCapturing) return;

            _isCapturing = false;
            _captureCts?.Cancel();
            _capture?.StopRecording();
            _processingThread?.Join(2000);

            CaptureStatusChanged?.Invoke(this, false);
            Logger.LogMessage("Audio capture stopped", "audio");
        }

        /// <summary>
        /// Reads from the conversion chain in exact 352-frame chunks and raises AudioDataAvailable.
        /// Sleeps 1ms when the buffer is empty to avoid spinning.
        /// </summary>
        private void ProcessingLoop(CancellationToken token)
        {
            int bytesPerPacket = _framesPerPacket * TARGET_CHANNELS * (TARGET_BITS / 8);
            var packetBuffer = new byte[bytesPerPacket];

            while (!token.IsCancellationRequested && _isCapturing)
            {
                try
                {
                    int totalRead = 0;
                    while (totalRead < bytesPerPacket && !token.IsCancellationRequested)
                    {
                        int read = _convertedProvider.Read(packetBuffer, totalRead, bytesPerPacket - totalRead);
                        if (read == 0)
                            Thread.Sleep(1);
                        else
                            totalRead += read;
                    }

                    if (totalRead == bytesPerPacket)
                    {
                        var copy = new byte[bytesPerPacket];
                        Buffer.BlockCopy(packetBuffer, 0, copy, 0, bytesPerPacket);
                        AudioDataAvailable?.Invoke(this, copy);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Processing loop error: {ex.Message}");
                    Thread.Sleep(10);
                }
            }

            Logger.LogMessage("Audio capture processing loop ended", "audio");
        }

        /// <summary>
        /// Enumerates active render devices and returns the first virtual audio cable found.
        /// Returns null to fall back to the system default render device.
        /// </summary>
        private static MMDevice FindBestCaptureDevice()
        {
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

                foreach (var device in devices)
                {
                    var name = device.FriendlyName;
                    if (name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Virtual Audio Cable", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.LogMessage($"Found virtual audio device: {name}", "audio");
                        return device;
                    }
                }

                Logger.LogMessage("No virtual audio device found, using default render device for loopback", "audio");
                return null;
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Device enumeration failed: {ex.Message}", "audio");
                return null;
            }
        }

        public int GetBytesPerFrame() => TARGET_CHANNELS * (TARGET_BITS / 8);
        public int GetBytesPerPacket() => _framesPerPacket * GetBytesPerFrame();

        public void Dispose()
        {
            StopCapture();
            _captureCts?.Dispose();
            _capture?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
