namespace WinStream.Core.Streaming;

/// <summary>
/// Decides when a running Extreme session should admit it is not keeping up.
/// </summary>
/// <remarks>
/// Auto answers sustained pressure by climbing the latency ladder, but Extreme
/// pins L at one packet and disables Auto, so a struggling session would otherwise
/// stutter indefinitely with nothing said. Pressure detection itself stays in
/// <see cref="LatencyAutoController"/>; this only adds the hysteresis that keeps a
/// single Wi-Fi hiccup from raising a warning.
/// </remarks>
public sealed class ExtremePressureHysteresis
{
    /// <summary>Pressure windows in a row before the warning appears.</summary>
    public const int ConsecutiveWindowsToWarn = 2;

    private int _streak;

    public bool IsWarningVisible { get; private set; }

    /// <summary>
    /// Feeds one evaluated signal window and returns whether the warning should be
    /// visible. A clean window clears it, so the caller can assign the result
    /// straight to the warning's visibility every window.
    /// </summary>
    public bool ObserveWindow(bool pressureThisWindow)
    {
        if (!pressureThisWindow)
        {
            Reset();
            return false;
        }

        if (_streak < ConsecutiveWindowsToWarn)
        {
            _streak++;
        }

        IsWarningVisible = _streak >= ConsecutiveWindowsToWarn;
        return IsWarningVisible;
    }

    public void Reset()
    {
        _streak = 0;
        IsWarningVisible = false;
    }
}
