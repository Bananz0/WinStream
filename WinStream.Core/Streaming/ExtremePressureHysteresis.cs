namespace WinStream.Core.Streaming;

/// <summary>
/// Decides when a running Extreme session should admit it is not keeping up.
/// </summary>
/// <remarks>
/// Auto answers sustained pressure by climbing the latency ladder. Extreme climbs a
/// short ladder then pins at Experimental's floor; this hysteresis only arms the
/// exhausted warning so a single Wi-Fi hiccup at the ceiling does not nag.
/// </remarks>
public sealed class ExtremePressureHysteresis
{
    /// <summary>Pressure windows in a row before the warning appears.</summary>
    public const int ConsecutiveWindowsToWarn = 2;

    /// <summary>
    /// Clean windows required before a visible warning disappears. Signal windows
    /// are two seconds, so this keeps the message readable for roughly ten seconds
    /// after pressure subsides instead of flashing the surrounding layout.
    /// </summary>
    public const int ConsecutiveCleanWindowsToClear = 5;

    private int _streak;
    private int _cleanStreak;

    public bool IsWarningVisible { get; private set; }

    /// <summary>
    /// Feeds one evaluated signal window and returns whether the warning should be
    /// visible. Once shown, several clean windows are required to clear it so the
    /// user has enough time to read and act on the message.
    /// </summary>
    public bool ObserveWindow(bool pressureThisWindow)
    {
        if (pressureThisWindow)
        {
            _cleanStreak = 0;
            if (IsWarningVisible)
            {
                return true;
            }

            if (_streak < ConsecutiveWindowsToWarn)
            {
                _streak++;
            }

            IsWarningVisible = _streak >= ConsecutiveWindowsToWarn;
            return IsWarningVisible;
        }

        _streak = 0;
        if (!IsWarningVisible)
        {
            return false;
        }

        _cleanStreak++;
        if (_cleanStreak >= ConsecutiveCleanWindowsToClear)
        {
            Reset();
        }

        return IsWarningVisible;
    }

    public void Reset()
    {
        _streak = 0;
        _cleanStreak = 0;
        IsWarningVisible = false;
    }
}
