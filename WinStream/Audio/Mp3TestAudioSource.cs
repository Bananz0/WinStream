using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using WinStream.Network;

namespace WinStream.Audio
{
    /// <summary>
    /// Test audio source that reads MP3 content (URL or local file), decodes to PCM
    /// and emits fixed-size packets compatible with the RTP streamer pipeline.
    /// </summary>
    public sealed class Mp3TestAudioSource : IDisposable
    {
        private readonly int _targetSampleRate;
        private readonly int _targetChannels;
        private readonly int _targetBitsPerSample;
        private readonly int _framesPerPacket;

        private string _workingFilePath;
        private bool _deleteWorkingFileOnDispose;

        private AudioFileReader _audioReader;
        private IWaveProvider _pcmProvider;

        private CancellationTokenSource _cts;
        private Task _producerTask;
        private bool _isRunning;

        public event EventHandler<byte[]> AudioDataAvailable;

        public bool IsRunning => _isRunning;
        public string SourceDescription { get; private set; } = "MP3 test source";

        public Mp3TestAudioSource(int sampleRate, int channels, int bitsPerSample, int framesPerPacket)
        {
            _targetSampleRate = sampleRate;
            _targetChannels = channels;
            _targetBitsPerSample = bitsPerSample;
            _framesPerPacket = framesPerPacket;
        }

        public async Task<bool> InitializeAsync(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            try
            {
                _workingFilePath = await ResolveSourceToFileAsync(source);
                _audioReader = new AudioFileReader(_workingFilePath);

                ISampleProvider sampleProvider = _audioReader;

                // Normalize channel layout to stereo for the RTP pipeline.
                if (_audioReader.WaveFormat.Channels == 1 && _targetChannels == 2)
                {
                    sampleProvider = new MonoToStereoSampleProvider(sampleProvider);
                }
                else if (_audioReader.WaveFormat.Channels > _targetChannels)
                {
                    var mux = new MultiplexingSampleProvider(new[] { sampleProvider }, _targetChannels);
                    mux.ConnectInputToOutput(0, 0);
                    mux.ConnectInputToOutput(1, 1);
                    sampleProvider = mux;
                }

                if (sampleProvider.WaveFormat.SampleRate != _targetSampleRate)
                {
                    sampleProvider = new WdlResamplingSampleProvider(sampleProvider, _targetSampleRate);
                }

                if (_targetBitsPerSample != 16)
                {
                    throw new NotSupportedException("MP3 test source currently supports only 16-bit PCM output.");
                }

                _pcmProvider = new SampleToWaveProvider16(sampleProvider);
                SourceDescription = source;

                Logger.LogMessage($"MP3 test source initialized: {source}", "audio");
                Logger.LogMessage($"Decoded format: {_audioReader.WaveFormat}", "audio");
                Logger.LogMessage($"Output format: {_targetSampleRate}Hz, {_targetChannels}ch, {_targetBitsPerSample}bit", "audio");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
                Logger.LogMessage($"Failed to initialize MP3 test source: {ex.Message}", "audio");
                return false;
            }
        }

        public void Start()
        {
            if (_isRunning || _pcmProvider == null)
            {
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _producerTask = Task.Run(() => ProducerLoopAsync(_cts.Token));

            Logger.LogMessage("MP3 test source started", "audio");
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            _cts?.Cancel();

            try
            {
                _producerTask?.Wait(2000);
            }
            catch
            {
                // Best-effort stop.
            }

            Logger.LogMessage("MP3 test source stopped", "audio");
        }

        private async Task ProducerLoopAsync(CancellationToken token)
        {
            int bytesPerPacket = _framesPerPacket * _targetChannels * (_targetBitsPerSample / 8);
            var packet = new byte[bytesPerPacket];
            int packetIntervalMs = (_framesPerPacket * 1000) / _targetSampleRate;

            while (!token.IsCancellationRequested && _isRunning)
            {
                try
                {
                    int totalRead = 0;
                    while (totalRead < bytesPerPacket && !token.IsCancellationRequested)
                    {
                        int read = _pcmProvider.Read(packet, totalRead, bytesPerPacket - totalRead);
                        if (read == 0)
                        {
                            // Loop file when we reach EOF.
                            _audioReader.Position = 0;
                            continue;
                        }
                        totalRead += read;
                    }

                    if (totalRead == bytesPerPacket)
                    {
                        var copy = new byte[bytesPerPacket];
                        Buffer.BlockCopy(packet, 0, copy, 0, bytesPerPacket);
                        AudioDataAvailable?.Invoke(this, copy);
                        await Task.Delay(packetIntervalMs, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogMessage($"MP3 source loop error: {ex.Message}", "audio");
                    await Task.Delay(10, token);
                }
            }
        }

        private async Task<string> ResolveSourceToFileAsync(string source)
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                var data = await client.GetByteArrayAsync(uri);

                var tempPath = Path.Combine(Path.GetTempPath(), $"winstream-test-{Guid.NewGuid():N}.mp3");
                await File.WriteAllBytesAsync(tempPath, data);
                _deleteWorkingFileOnDispose = true;
                return tempPath;
            }

            if (File.Exists(source))
            {
                return source;
            }

            throw new FileNotFoundException($"Audio source not found: {source}");
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
            _audioReader?.Dispose();
            _audioReader = null;
            _pcmProvider = null;

            if (_deleteWorkingFileOnDispose && !string.IsNullOrWhiteSpace(_workingFilePath))
            {
                try
                {
                    File.Delete(_workingFilePath);
                }
                catch
                {
                    // Non-fatal.
                }
            }
        }
    }
}
