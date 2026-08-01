namespace WinStream.Core.Streaming;

using WinStream.Core.Protocol.Raop;

/// <summary>
/// Raise-only AirPlay latency ladder driven by sender-side delivery pressure.
/// Pure Core logic — no WASAPI or UI dependencies.
/// </summary>
/// <remarks>
/// Auto climbs by <see cref="StepFrames"/> toward <see cref="CeilingFrames"/>.
/// Extreme (LabPacket) climbs a short TuneBlade-style ladder
/// 2112 → 3520 → 11025, then stops so the UI can offer Experimental.
/// </remarks>
public sealed class LatencyAutoController
{
    /// <summary>Auto starts at the folklore SETUP min (~250 ms) and may climb.</summary>
    public const uint AutoStartFrames = 11025;

    public const uint StepFrames = 11025;
    public const uint CeilingFrames = 88200;
    public const uint LowDelayFrames = 44100;
    public const uint BalancedFrames = 66150;
    public const uint MostStableFrames = 88200;
    public const uint VeryLowFrames = 22050;
    public const uint ExperimentalFrames = 11025;

    /// <summary>Absolute minimum frames for SetEffectiveLatencyFrames (packet floor).</summary>
    public const uint PacketFloorFrames = AlacEncoder.FramesPerPacket;

    /// <summary>
    /// Extreme RealTime ask: six ALAC packets ≈ 47.9 ms (UI: Extreme ~50 ms).
    /// </summary>
    public const uint LabPacketFrames = 2112;

    /// <summary>Extreme mid rung ≈ 80 ms (10 packets) after the first raise.</summary>
    public const uint ExtremeMidFrames = 3520;

    /// <summary>Extreme ladder top — Experimental folklore floor.</summary>
    public const uint ExtremeCeilingFrames = ExperimentalFrames;

    /// <summary>Apple SETUP folklore min (~250 ms). Used when L ≥ this value.</summary>
    public const uint LatencyMinFrames = 11025;

    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Extreme only has two raise steps; a 30 s Auto cool-down would stall recovery
    /// during continuous underrun under today's 50 ms capture.
    /// </summary>
    public static readonly TimeSpan ExtremeCoolDown = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SignalWindow = TimeSpan.FromSeconds(2);

    public const int QueueDropRaiseThreshold = 3;
    public const int SlowSendRaiseThreshold = 5;

    private uint _effectiveFrames = AutoStartFrames;
    private DateTimeOffset _lastRaiseUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _audioStartedUtc = DateTimeOffset.MinValue;
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
    /// Resets for a new user connect / first session. Auto always restarts at the floor.
    /// Extreme restarts at 2112. A resilience reconnect rebuilds the session with the
    /// current effective frames and does not call this method, so a climbed step survives.
    /// </summary>
    public void ResetForConnect(PlaybackResponsiveness mode)
    {
        _autoEnabled = mode == PlaybackResponsiveness.Auto;
        _extremeRaiseEnabled = mode == PlaybackResponsiveness.LabPacket;
        _effectiveFrames = ResolveFixedFrames(mode);
        _lastRaiseUtc = DateTimeOffset.MinValue;
        _audioStartedUtc = DateTimeOffset.MinValue;
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
    public static bool HasPressure(long queueDropsInWindow, long slowSendsInWindow) =>
        queueDropsInWindow >= QueueDropRaiseThreshold ||
        slowSendsInWindow >= SlowSendRaiseThreshold;

    /// <summary>
    /// Attempts a raise when Auto or Extreme raise mode is enabled and pressure
    /// signals cross thresholds.
    /// </summary>
    public bool TryRaise(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow)
    {
        if (_extremeRaiseEnabled)
        {
            return TryRaiseExtreme(
                queueDropsInWindow,
                slowSendsInWindow,
                isStreaming,
                isSilent,
                utcNow);
        }

        if (!_autoEnabled || !isStreaming || isSilent)
        {
            return false;
        }

        if (!IsPastStartupGrace(utcNow))
        {
            return false;
        }

        if (_effectiveFrames >= CeilingFrames)
        {
            return false;
        }

        if (_lastRaiseUtc != DateTimeOffset.MinValue &&
            utcNow - _lastRaiseUtc < CoolDown)
        {
            return false;
        }

        if (!HasPressure(queueDropsInWindow, slowSendsInWindow))
        {
            return false;
        }

        _effectiveFrames = Math.Min(CeilingFrames, _effectiveFrames + StepFrames);
        _lastRaiseUtc = utcNow;
        return true;
    }

    private bool TryRaiseExtreme(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow)
    {
        if (!isStreaming || isSilent)
        {
            return false;
        }

        if (!IsPastStartupGrace(utcNow))
        {
            return false;
        }

        if (_effectiveFrames >= ExtremeCeilingFrames)
        {
            return false;
        }

        if (_lastRaiseUtc != DateTimeOffset.MinValue &&
            utcNow - _lastRaiseUtc < ExtremeCoolDown)
        {
            return false;
        }

        if (!HasPressure(queueDropsInWindow, slowSendsInWindow))
        {
            return false;
        }

        _effectiveFrames = _effectiveFrames < ExtremeMidFrames
            ? ExtremeMidFrames
            : ExtremeCeilingFrames;
        _lastRaiseUtc = utcNow;
        return true;
    }

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
