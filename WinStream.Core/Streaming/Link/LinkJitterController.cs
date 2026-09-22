namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Raise/shrink jitter target in milliseconds for WinStream Link RX.
/// </summary>
public sealed class LinkJitterController
{
    public const int EthernetStartMs = 3;
    public const int OtherStartMs = 8;
    public const int MinMs = 2;
    public const int MaxMs = 60;
    public const int GrowStepMs = 2;
    public const int ShrinkStepMs = 1;

    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ShrinkAfterClean = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(1);

    private int _targetMs;
    private DateTimeOffset _lastChangeUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _startedUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPressureUtc = DateTimeOffset.MinValue;
    private readonly int _floorMs;

    public LinkJitterController(bool pathIsEthernet = true)
    {
        _floorMs = pathIsEthernet ? EthernetStartMs : OtherStartMs;
        _targetMs = _floorMs;
    }

    public int TargetMilliseconds => _targetMs;

    public void MarkStarted(DateTimeOffset utcNow) => _startedUtc = utcNow;

    /// <summary>
    /// Grow on late/underrun pressure; shrink slowly after a clean window.
    /// </summary>
    public bool TryUpdate(
        bool hadLateOrUnderrun,
        DateTimeOffset utcNow)
    {
        if (_startedUtc == DateTimeOffset.MinValue)
        {
            return false;
        }

        if (utcNow - _startedUtc < StartupGrace)
        {
            return false;
        }

        if (hadLateOrUnderrun)
        {
            _lastPressureUtc = utcNow;
            if (_lastChangeUtc != DateTimeOffset.MinValue &&
                utcNow - _lastChangeUtc < CoolDown)
            {
                return false;
            }

            if (_targetMs >= MaxMs)
            {
                return false;
            }

            _targetMs = Math.Min(MaxMs, _targetMs + GrowStepMs);
            _lastChangeUtc = utcNow;
            return true;
        }

        if (_lastPressureUtc != DateTimeOffset.MinValue &&
            utcNow - _lastPressureUtc < ShrinkAfterClean)
        {
            return false;
        }

        if (_lastChangeUtc != DateTimeOffset.MinValue &&
            utcNow - _lastChangeUtc < CoolDown)
        {
            return false;
        }

        if (_targetMs <= MinMs)
        {
            return false;
        }

        var floor = Math.Max(MinMs, _floorMs);
        if (_targetMs <= floor)
        {
            return false;
        }

        _targetMs = Math.Max(floor, _targetMs - ShrinkStepMs);
        _lastChangeUtc = utcNow;
        return true;
    }
}
