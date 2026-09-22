namespace WinStream.Core.Streaming;

using WinStream.Core.Audio;
using WinStream.Core.Protocol.Raop;

/// <summary>
/// Dynamic AirPlay latency control driven by sender-side delivery pressure.
/// Auto starts at the lowest practical floor (~50 ms) and adjusts up or down.
/// Extreme (LabPacket) is raise-only through a short ladder.
/// </summary>
public sealed class LatencyAutoController
{
    /// <summary>Auto starts at the lowest rung (~50 ms) and adjusts dynamically.</summary>
    public const uint AutoStartFrames = LabPacketFrames;

    public const uint StepFrames = 11025;
    public const uint CeilingFrames = 88200;
    public const uint LowDelayFrames = 44100;
    public const uint BalancedFrames = 66150;
    public const uint MostStableFrames = 88200;
    public const uint VeryLowFrames = 22050;
    public const uint ExperimentalFrames = 11025;

    /// <summary>Absolute minimum frames for SetEffectiveLatencyFrames (packet floor).</summary>
    public const uint PacketFloorFrames = AudioPacingConstants.PacketFrames;

    /// <summary>
    /// Extreme RealTime ask: six ALAC packets ≈ 47.9 ms (UI: Extreme ~50 ms).
    /// </summary>
    public const uint LabPacketFrames = 2112;

    /// <summary>Extreme / Auto mid rung ≈ 80 ms (10 packets).</summary>
    public const uint ExtremeMidFrames = 3520;

    /// <summary>Extreme ladder top — Experimental folklore floor.</summary>
    public const uint ExtremeCeilingFrames = ExperimentalFrames;

    /// <summary>Apple SETUP folklore min (~250 ms). Used when L ≥ this value.</summary>
    public const uint LatencyMinFrames = 11025;

    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shorter cool-down while Auto or Extreme is still on the low rungs so the
    /// controller can react before audible underruns stack up.
    /// </summary>
    public static readonly TimeSpan LowRungCoolDown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Wait after the last raise before Auto may step down — avoids yo-yo around a spike.
    /// </summary>
    public static readonly TimeSpan LowerCoolDown = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SignalWindow = TimeSpan.FromSeconds(2);

    public const int QueueDropRaiseThreshold = 3;
    public const int SlowSendRaiseThreshold = 5;

    /// <summary>
    /// Timeline re-anchors in one signal window. Each clamp drops late packets and
    /// bursts the next ones — a common crackle source that queue drops alone miss.
    /// </summary>
    public const int ReanchorRaiseThreshold = 1;

    /// <summary>
    /// Consecutive clean signal windows required before Auto steps down one rung.
    /// </summary>
    public const int LowerCleanWindowsThreshold = 3;

    private uint _effectiveFrames = AutoStartFrames;
    private DateTimeOffset _lastRaiseUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLowerUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _audioStartedUtc = DateTimeOffset.MinValue;
    private int _consecutiveCleanWindows;
    private bool _autoEnabled = true;
    private bool _extremeRaiseEnabled;

    public uint EffectiveFrames => _effectiveFrames;

    public bool IsAutoEnabled => _autoEnabled;

    /// <summary>True while Extreme may still climb its short ladder.</summary>
    public bool IsExtremeRaiseEnabled => _extremeRaiseEnabled;

    /// <summary>
    /// True once Extreme has reached 11025. The InfoBar should only arm here —
    /// mid-ladder raises stay silent (AppLog only).
    /// </summary>
    public bool IsExtremeLadderExhausted =>
        _extremeRaiseEnabled && _effectiveFrames >= ExtremeCeilingFrames;

    /// <summary>
    /// Resets for a new user connect / first session. Auto restarts at the floor.
    /// Extreme restarts at 2112. A resilience reconnect rebuilds the session with the
    /// current effective frames and does not call this method, so a climbed step survives.
    /// </summary>
    public void ResetForConnect(PlaybackResponsiveness mode)
    {
        _autoEnabled = mode == PlaybackResponsiveness.Auto;
        _extremeRaiseEnabled = mode == PlaybackResponsiveness.LabPacket;
        _effectiveFrames = ResolveFixedFrames(mode);
        _lastRaiseUtc = DateTimeOffset.MinValue;
        _lastLowerUtc = DateTimeOffset.MinValue;
        _audioStartedUtc = DateTimeOffset.MinValue;
        _consecutiveCleanWindows = 0;
    }

    public void MarkAudioStarted(DateTimeOffset utcNow) =>
        _audioStartedUtc = utcNow;

    /// <summary>
    /// True once audio has been flowing longer than <see cref="StartupGrace"/>. Shared
    /// with the Extreme pressure warning so both read one definition of "settled".
    /// </summary>
    public bool IsPastStartupGrace(DateTimeOffset utcNow) =>
        _audioStartedUtc != DateTimeOffset.MinValue &&
        utcNow - _audioStartedUtc >= StartupGrace;

    /// <summary>Sender-side delivery pressure over one signal window.</summary>
    public static bool HasPressure(
        long queueDropsInWindow,
        long slowSendsInWindow,
        long reanchorsInWindow = 0) =>
        queueDropsInWindow >= QueueDropRaiseThreshold ||
        slowSendsInWindow >= SlowSendRaiseThreshold ||
        reanchorsInWindow >= ReanchorRaiseThreshold;

    /// <summary>
    /// Auto-only: raises on pressure, lowers after sustained clean windows.
    /// Returns true when effective frames changed.
    /// </summary>
    public bool TryAdjustAuto(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow,
        long reanchorsInWindow = 0)
    {
        if (!_autoEnabled || !isStreaming || isSilent)
        {
            return false;
        }

        if (!IsPastStartupGrace(utcNow))
        {
            return false;
        }

        if (HasPressure(queueDropsInWindow, slowSendsInWindow, reanchorsInWindow))
        {
            _consecutiveCleanWindows = 0;
            return TryRaiseAuto(utcNow);
        }

        _consecutiveCleanWindows++;
        return TryLowerAuto(utcNow);
    }

    /// <summary>
    /// Extreme raise-only ladder. Auto uses <see cref="TryAdjustAuto"/> instead.
    /// </summary>
    public bool TryRaiseExtreme(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow,
        long reanchorsInWindow = 0)
    {
        if (!_extremeRaiseEnabled ||
            !CanEvaluate(isStreaming, isSilent, utcNow) ||
            _effectiveFrames >= ExperimentalFrames ||
            !HasPressure(queueDropsInWindow, slowSendsInWindow, reanchorsInWindow))
        {
            return false;
        }

        if (_lastRaiseUtc != DateTimeOffset.MinValue &&
            utcNow - _lastRaiseUtc < LowRungCoolDown)
        {
            return false;
        }

        var next = Math.Min(NextRungUp(_effectiveFrames), ExperimentalFrames);
        if (next == _effectiveFrames)
        {
            return false;
        }

        _effectiveFrames = next;
        _lastRaiseUtc = utcNow;
        return true;
    }

    /// <summary>Legacy entry for callers that still invoke <see cref="TryRaise"/>.</summary>
    public bool TryRaise(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow) =>
        TryRaiseExtreme(queueDropsInWindow, slowSendsInWindow, isStreaming, isSilent, utcNow);

    /// <summary>Next rung when stepping up from the current effective latency.</summary>
    public static uint NextRungUp(uint currentFrames)
    {
        if (currentFrames < ExtremeMidFrames)
        {
            return ExtremeMidFrames;
        }

        if (currentFrames < ExperimentalFrames)
        {
            return ExperimentalFrames;
        }

        var stepped = currentFrames + StepFrames;
        return stepped > CeilingFrames ? CeilingFrames : stepped;
    }

    /// <summary>Next rung when stepping down from the current effective latency.</summary>
    public static uint NextRungDown(uint currentFrames)
    {
        if (currentFrames > ExperimentalFrames)
        {
            var stepped = currentFrames - StepFrames;
            return stepped < ExperimentalFrames ? ExperimentalFrames : stepped;
        }

        if (currentFrames > ExtremeMidFrames)
        {
            return ExtremeMidFrames;
        }

        return LabPacketFrames;
    }

    private bool TryRaiseAuto(DateTimeOffset utcNow)
    {
        if (_effectiveFrames >= CeilingFrames)
        {
            return false;
        }

        var coolDown = _effectiveFrames < ExperimentalFrames ? LowRungCoolDown : CoolDown;
        if (_lastRaiseUtc != DateTimeOffset.MinValue &&
            utcNow - _lastRaiseUtc < coolDown)
        {
            return false;
        }

        var next = NextRungUp(_effectiveFrames);
        if (next == _effectiveFrames)
        {
            return false;
        }

        _effectiveFrames = next;
        _lastRaiseUtc = utcNow;
        return true;
    }

    private bool TryLowerAuto(DateTimeOffset utcNow)
    {
        if (_effectiveFrames <= AutoStartFrames)
        {
            return false;
        }

        if (_consecutiveCleanWindows < LowerCleanWindowsThreshold)
        {
            return false;
        }

        if (_lastRaiseUtc != DateTimeOffset.MinValue &&
            utcNow - _lastRaiseUtc < LowerCoolDown)
        {
            return false;
        }

        if (_lastLowerUtc != DateTimeOffset.MinValue &&
            utcNow - _lastLowerUtc < LowRungCoolDown)
        {
            return false;
        }

        var next = NextRungDown(_effectiveFrames);
        if (next >= _effectiveFrames)
        {
            return false;
        }

        _effectiveFrames = next;
        _lastLowerUtc = utcNow;
        _consecutiveCleanWindows = 0;
        return true;
    }

    private bool CanEvaluate(bool isStreaming, bool isSilent, DateTimeOffset utcNow) =>
        isStreaming && !isSilent && IsPastStartupGrace(utcNow);

    public static uint ResolveFixedFrames(PlaybackResponsiveness mode) => mode switch
    {
        PlaybackResponsiveness.VeryLow => VeryLowFrames,
        PlaybackResponsiveness.Experimental => ExperimentalFrames,
        PlaybackResponsiveness.LabPacket => LabPacketFrames,
        PlaybackResponsiveness.LowDelay => LowDelayFrames,
        PlaybackResponsiveness.Balanced => BalancedFrames,
        PlaybackResponsiveness.MostStable => MostStableFrames,
        _ => AutoStartFrames
    };

    /// <summary>SETUP latencyMin: folklore 11025, or lower when requesting a Lab-class lead.</summary>
    public static uint SetupLatencyMin(uint effectiveFrames) =>
        Math.Min(LatencyMinFrames, Math.Max(PacketFloorFrames, effectiveFrames));

    /// <summary>SETUP latencyMax: at least Apple 88200, or higher if L exceeds it.</summary>
    public static uint SetupLatencyMax(uint effectiveFrames) =>
        Math.Max(CeilingFrames, effectiveFrames);

    public static uint ClampEffectiveFrames(uint frames) =>
        Math.Max(PacketFloorFrames, frames);
}
