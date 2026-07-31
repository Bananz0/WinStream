namespace WinStream.Core.Streaming;

using WinStream.Core.Protocol.Raop;

/// <summary>
/// Raise-only AirPlay latency ladder driven by sender-side delivery pressure.
/// Pure Core logic — no WASAPI or UI dependencies.
/// </summary>
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

    /// <summary>One ALAC packet — absolute Lab probe floor.</summary>
    public const uint LabPacketFrames = PacketFloorFrames;

    /// <summary>Apple SETUP folklore min (~250 ms). Used when L ≥ this value.</summary>
    public const uint LatencyMinFrames = 11025;

    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SignalWindow = TimeSpan.FromSeconds(2);

    public const int QueueDropRaiseThreshold = 3;
    public const int SlowSendRaiseThreshold = 5;

    private uint _effectiveFrames = AutoStartFrames;
    private DateTimeOffset _lastRaiseUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _audioStartedUtc = DateTimeOffset.MinValue;
    private bool _autoEnabled = true;

    public uint EffectiveFrames => _effectiveFrames;

    public bool IsAutoEnabled => _autoEnabled;

    /// <summary>
    /// Resets for a new user connect / first session. Auto always restarts at the floor.
    /// A resilience reconnect rebuilds the session with the current effective frames and
    /// does not call this method, so a climbed Auto step survives the reconnect.
    /// </summary>
    public void ResetForConnect(PlaybackResponsiveness mode)
    {
        _autoEnabled = mode == PlaybackResponsiveness.Auto;
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
    /// Attempts a raise when Auto is enabled and pressure signals cross thresholds.
    /// </summary>
    public bool TryRaise(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow)
    {
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
