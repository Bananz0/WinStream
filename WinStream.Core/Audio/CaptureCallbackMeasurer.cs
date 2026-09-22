using System.Diagnostics;

namespace WinStream.Core.Audio;

/// <summary>
/// Measures the rolling p95 interval between capture callbacks. Requested client-buffer
/// sizes are not accepted as evidence of capture latency.
/// </summary>
public sealed class CaptureCallbackMeasurer
{
    public const int DefaultWindowSize = 64;
    public const int DefaultWarmupSamples = 16;

    private readonly object _gate = new();
    private readonly double[] _samples;
    private readonly int _warmupSamples;
    private readonly long _frequencyHz;
    private int _count;
    private int _next;

    public CaptureCallbackMeasurer(
        int windowSize = DefaultWindowSize,
        int warmupSamples = DefaultWarmupSamples,
        long? frequencyHz = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(warmupSamples, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(warmupSamples, windowSize);

        _samples = new double[windowSize];
        _warmupSamples = warmupSamples;
        _frequencyHz = frequencyHz ?? Stopwatch.Frequency;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_frequencyHz, 0);
    }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _count >= _warmupSamples;
            }
        }
    }

    public int MeasuredContributionMilliseconds
    {
        get
        {
            lock (_gate)
            {
                if (_count < _warmupSamples)
                {
                    return 0;
                }

                var ordered = _samples.AsSpan(0, _count).ToArray();
                Array.Sort(ordered);
                var index = Math.Clamp(
                    (int)Math.Ceiling(ordered.Length * 0.95) - 1,
                    0,
                    ordered.Length - 1);
                return (int)Math.Ceiling(ordered[index]);
            }
        }
    }

    public void RecordInterval(long deltaTicks)
    {
        if (deltaTicks <= 0)
        {
            return;
        }

        var milliseconds = deltaTicks * 1000.0 / _frequencyHz;
        lock (_gate)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
            {
                _count++;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_samples);
            _count = 0;
            _next = 0;
        }
    }
}
