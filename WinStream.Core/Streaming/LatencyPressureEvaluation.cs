namespace WinStream.Core.Streaming;

/// <summary>
/// Pure evaluation of one closed pressure signal window. Keeps orchestrator wiring
/// testable without WinUI or a live send pump.
/// </summary>
public static class LatencyPressureEvaluation
{
    public readonly record struct WindowSignals(
        long DropDelta,
        long SlowDelta,
        long ReanchorDelta,
        bool IsStreaming,
        bool IsSilent,
        DateTimeOffset Now);

    public readonly record struct Outcome(
        bool LatencyChanged,
        uint EffectiveFrames,
        string ModeLabel,
        bool ClearExtremePressureBanner);

    /// <summary>
    /// Marks audio started on first non-silent sample. Returns true when callers
    /// should reset window counters without evaluating pressure yet.
    /// </summary>
    public static bool TryMarkAudioStarted(
        ref bool audioStartedMarked,
        bool isSilent,
        DateTimeOffset now,
        LatencyAutoController latency)
    {
        if (audioStartedMarked || isSilent)
        {
            return false;
        }

        latency.MarkAudioStarted(now);
        audioStartedMarked = true;
        return true;
    }

    /// <summary>
    /// Applies Auto or Extreme raise logic for one closed signal window.
    /// </summary>
    public static Outcome EvaluateLatencyChange(
        LatencyAutoController latency,
        in WindowSignals signals)
    {
        if (latency.IsAutoEnabled)
        {
            if (!latency.TryAdjustAuto(
                    signals.DropDelta,
                    signals.SlowDelta,
                    signals.IsStreaming,
                    signals.IsSilent,
                    signals.Now,
                    signals.ReanchorDelta))
            {
                return default;
            }

            return new Outcome(
                LatencyChanged: true,
                latency.EffectiveFrames,
                ModeLabel: "Auto",
                ClearExtremePressureBanner: false);
        }

        if (!latency.IsExtremeRaiseEnabled)
        {
            return default;
        }

        if (!latency.TryRaiseExtreme(
                signals.DropDelta,
                signals.SlowDelta,
                signals.IsStreaming,
                signals.IsSilent,
                signals.Now,
                signals.ReanchorDelta))
        {
            return default;
        }

        return new Outcome(
            LatencyChanged: true,
            latency.EffectiveFrames,
            ModeLabel: "Extreme",
            ClearExtremePressureBanner: !latency.IsExtremeLadderExhausted);
    }

    /// <summary>
    /// Evaluates whether the Extreme exhausted banner visibility changed this window.
    /// </summary>
    public static bool? EvaluateExtremePressureBanner(
        LatencyAutoController latency,
        ExtremePressureHysteresis hysteresis,
        in WindowSignals signals)
    {
        if (!latency.IsExtremeRaiseEnabled)
        {
            return null;
        }

        var eligible = CaptureModePolicy.ArmsExhaustedPressureBanner(
            latency.IsExtremeLadderExhausted,
            signals.IsStreaming,
            signals.IsSilent,
            latency.IsPastStartupGrace(signals.Now));

        var pressure = eligible &&
                       LatencyAutoController.HasPressure(
                           signals.DropDelta,
                           signals.SlowDelta,
                           signals.ReanchorDelta);
        var wasVisible = hysteresis.IsWarningVisible;
        var visible = hysteresis.ObserveWindow(pressure);
        return visible == wasVisible ? null : visible;
    }
}
