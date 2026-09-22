using System;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Network;

namespace WinStream.Audio
{
    internal sealed class GeneratedTestAudioSource : IDisposable
    {
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly int _framesPerPacket;
        private readonly int _frequencyHz;
        private readonly short _amplitude;

        private CancellationTokenSource _cts;
        private Task _producerTask;
        private bool _isRunning;
        private int _sampleIndex;

        public GeneratedTestAudioSource(
            int sampleRate,
            int channels,
            int bitsPerSample,
            int framesPerPacket,
            int frequencyHz = 440,
            short amplitude = 8000)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _bitsPerSample = bitsPerSample;
            _framesPerPacket = framesPerPacket;
            _frequencyHz = frequencyHz;
            _amplitude = amplitude;
        }

        public event EventHandler<byte[]> AudioDataAvailable;

        public string SourceDescription => $"Generated tone {_frequencyHz}Hz";

        public Task<bool> InitializeAsync()
        {
            return Task.FromResult(_channels == 2 && _bitsPerSample == 16);
        }

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();
            _producerTask = Task.Run(() => ProducerLoopAsync(_cts.Token));
            Logger.LogMessage($"Generated test audio source started: {SourceDescription}", "audio");
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
            }

            Logger.LogMessage("Generated test audio source stopped", "audio");
        }

        private async Task ProducerLoopAsync(CancellationToken token)
        {
            int bytesPerPacket = _framesPerPacket * _channels * (_bitsPerSample / 8);
            int packetIntervalMs = (_framesPerPacket * 1000) / _sampleRate;

            while (!token.IsCancellationRequested && _isRunning)
            {
                var packet = new byte[bytesPerPacket];
                for (int frame = 0; frame < _framesPerPacket; frame++, _sampleIndex++)
                {
                    double time = (double)_sampleIndex / _sampleRate;
                    short sample = (short)(Math.Sin(2 * Math.PI * _frequencyHz * time) * _amplitude);
                    int offset = frame * _channels * (_bitsPerSample / 8);
                    for (int channel = 0; channel < _channels; channel++)
                    {
                        packet[offset + (channel * 2)] = (byte)(sample & 0xFF);
                        packet[offset + (channel * 2) + 1] = (byte)((sample >> 8) & 0xFF);
                    }
                }

                AudioDataAvailable?.Invoke(this, packet);

                try
                {
                    await Task.Delay(packetIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }
    }
}
