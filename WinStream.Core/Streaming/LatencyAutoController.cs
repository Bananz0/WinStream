namespace WinStream.Core.Streaming;

using WinStream.Core.Audio;
using WinStream.Core.Protocol.Raop;

/// <summary>
/// Dynamic AirPlay latency control driven by sender-side delivery pressure.
/// Auto starts at 250 ms (11025 frames) and settles no lower than an 80 ms
/// floor (3520 frames) after sustained clean delivery. Extreme (LabPacket) is
/// the only mode that can reach the 48 ms floor (2112 frames), through its own
/// short raise-only ladder.
///
/// Raises are cheap: an immediate signal (an actual scheduler stall, a filled
/// capture gap, or a reported PTP re-anchor) raises right away, inside the
/// hole that already happened — no cooldown. A leading-indicator signal
/// (queue drops / slow sends trending up on otherwise-clean audio) raises one
/// rung after a short cooldown. Lowers are rare: at most one rung, gated on a
/// detected silence window, only after a long clean streak, and protected by
/// a decaying ratchet so a rung the controller was just forced back into
/// cannot be lowered out of again until a growing (but never permanent)
/// cooldown elapses.
/// </summary>
public sealed class LatencyAutoController
{
    /// <summary>Auto's new default starting rung: 250 ms.</summary>
    public const uint AutoStartFrames = ExperimentalFrames;

    /// <summary>Auto's floor after sustained clean delivery: 80 ms. Extreme-only below this.</summary>
    public const uint AutoFloorFrames = ExtremeMidFrames;

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
    /// Not reachable by Auto — Extreme-mode-only.
    /// </summary>
    public const uint LabPacketFrames = 2112;

    /// <summary>Extreme / Auto mid rung ≈ 80 ms (10 packets). Auto's floor.</summary>
    public const uint ExtremeMidFrames = 3520;

    /// <summary>Extreme ladder top — Experimental folklore floor.</summary>
    public const uint ExtremeCeilingFrames = ExperimentalFrames;

    /// <summary>Apple SETUP folklore min (~250 ms). Used when L ≥ this value.</summary>
    public const uint LatencyMinFrames = 11025;

    /// <summary>
    /// Auto's finer-grained rung ladder (~1.5x steps), used only for Auto's
    /// own raise/lower stepping. Extreme keeps its own short 3-rung ladder
    /// (<see cref="LabPacketFrames"/> → <see cref="ExtremeMidFrames"/> →
    /// <see cref="ExtremeCeilingFrames"/>) via <see cref="NextRungUp"/>.
    /// </summary>
    public static readonly uint[] AutoLadderFrames =
    [
        3520, 5280, 7920, 11025, 16538, 22050, 33075, 44100, 66150, 88200
    ];

    public static readonly TimeSpan CoolDown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Shorter cool-down while Extreme is still on the low rungs so the
    /// controller can react before audible underruns stack up.
    /// </summary>
    public static readonly TimeSpan LowRungCoolDown = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Tightened cool-down for Auto's leading-indicator (non-immediate) raises.
    /// Raises are cheap, so this is deliberately much shorter than
    /// <see cref="LowerCoolDown"/> — an immediate raise (stall/gap/re-anchor)
    /// bypasses this entirely.
    /// </summary>
    public static readonly TimeSpan AutoRaiseCoolDown = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Wait after the last raise before Auto may step down — avoids yo-yo around a spike.
    /// </summary>
    public static readonly TimeSpan LowerCoolDown = TimeSpan.FromSeconds(15);

    /// <summary>Shortened so Auto/Extreme can react quickly after audio starts.</summary>
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(2);

    public static readonly TimeSpan SignalWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Minimum contiguous silence within a signal window before Auto treats it
    /// as a safe re-anchor opportunity for lowering (windowed silence gate,
    /// replacing a bare single-sample check).
    /// </summary>
    public static readonly TimeSpan MinSilenceForLower = TimeSpan.FromMilliseconds(300);

    /// <summary>Starting size of the decaying lower-ratchet lockout.</summary>
    public const int RatchetBaseMinutes = 3;

    /// <summary>
    /// Ceiling on the ratchet lockout so a bad early burst can still recover
    /// on a genuinely clean network within hours, never for the rest of the
    /// session.
    /// </summary>
    public const int RatchetMaxMinutes = 32;

    public const int QueueDropRaiseThreshold = 3;
    public const int SlowSendRaiseThreshold = 5;

    /// <summary>
    /// Timeline re-anchors in one signal window. Each clamp drops late packets and
    /// bursts the next ones — a common crackle source that queue drops alone miss.
    /// </summary>
    public const int ReanchorRaiseThreshold = 1;

    /// <summary>
    /// Consecutive clean signal windows required before Auto may consider
    /// stepping down one rung (~120 s at the 2 s signal window) — a
    /// deliberately longer streak than the old 3-window check, since lowering
    /// still additionally requires a detected silence window on top of this.
    /// </summary>
    public const int LowerCleanWindowsThreshold = 60;

    private uint _effectiveFrames = AutoStartFrames;
    private DateTimeOffset _lastRaiseUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastLowerUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _audioStartedUtc = DateTimeOffset.MinValue;
    private int _consecutiveCleanWindows;
    private bool _autoEnabled = true;
    private bool _extremeRaiseEnabled;

    /// <summary>Rungs Auto has lowered out of at least once this session.</summary>
    private readonly HashSet<uint> _rungsLoweredFrom = [];

    /// <summary>How many times each rung has been forced back into after a lower.</summary>
    private readonly Dictionary<uint, int> _rungReRaiseCounts = new();

    /// <summary>Per-rung decaying lockout: cannot lower OUT OF this rung until this time.</summary>
    private readonly Dictionary<uint, DateTimeOffset> _rungLowerLockedUntil = new();

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
    /// Resets for a new user connect / first session. Auto restarts at the
    /// 250 ms default. Extreme restarts at 2112. A resilience reconnect
    /// rebuilds the session with the current effective frames and does not
    /// call this method, so a climbed step survives.
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
        _rungsLoweredFrom.Clear();
        _rungReRaiseCounts.Clear();
        _rungLowerLockedUntil.Clear();
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
    /// True when an already-unavoidable hole occurred this window (a real
    /// scheduler stall, a filled capture gap, or a reported re-anchor such as
    /// a PTP re-seed). These raise immediately, with no cooldown, because the
    /// artifact already happened — the raise itself adds nothing extra.
    /// </summary>
    public static bool HasImmediatePressure(
        long reanchorsInWindow,
        long ptpReseedDeltaInWindow = 0,
        long gapDeltaInWindow = 0) =>
        reanchorsInWindow > 0 || ptpReseedDeltaInWindow > 0 || gapDeltaInWindow > 0;

    /// <summary>
    /// Auto-only: raises on pressure (immediately for an already-unavoidable
    /// hole, on a short cooldown for a leading indicator on clean audio),
    /// lowers at most one rung after a detected silence window and a long
    /// clean streak. Returns true when effective frames changed.
    /// </summary>
    public bool TryAdjustAuto(
        long queueDropsInWindow,
        long slowSendsInWindow,
        bool isStreaming,
        bool isSilent,
        DateTimeOffset utcNow,
        long reanchorsInWindow = 0,
        long ptpReseedDeltaInWindow = 0,
        long gapDeltaInWindow = 0,
        TimeSpan silentDurationInWindow = default)
    {
        if (!_autoEnabled || !isStreaming)
        {
            return false;
        }

        if (!IsPastStartupGrace(utcNow))
        {
            return false;
        }

        if (HasImmediatePressure(reanchorsInWindow, ptpReseedDeltaInWindow, gapDeltaInWindow))
        {
            _consecutiveCleanWindows = 0;
            return TryRaiseAuto(utcNow, bypassCooldown: true);
        }

        var hasLeadingPressure =
            queueDropsInWindow >= QueueDropRaiseThreshold ||
            slowSendsInWindow >= SlowSendRaiseThreshold;

        if (!isSilent && hasLeadingPressure)
        {
            _consecutiveCleanWindows = 0;
            return TryRaiseAuto(utcNow, bypassCooldown: false);
        }

        _consecutiveCleanWindows++;

        // Windowed silence gate: a caller that only knows a per-window bool
        // (no finer-grained duration) is treated as fully silent for the
        // window, which already clears the (much smaller) minimum.
        var effectiveSilentDuration = silentDurationInWindow > TimeSpan.Zero
            ? silentDurationInWindow
            : isSilent ? SignalWindow : TimeSpan.Zero;

        if (isSilent && effectiveSilentDuration >= MinSilenceForLower)
        {
            return TryLowerAuto(utcNow);
        }

        return false;
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

    /// <summary>
    /// Next rung when stepping up from the current effective latency, on
    /// Extreme's short 3-rung ladder (also the historical Auto ladder, kept
    /// for callers that reference it directly, e.g. cross-protocol frame
    /// helpers). Auto's own stepping uses <see cref="NextAutoRungUp"/>.
    /// </summary>
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

    /// <summary>Next rung when stepping down, mirroring <see cref="NextRungUp"/>.</summary>
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

    /// <summary>
    /// Next rung up on Auto's finer (~1.5x) ladder — the first
    /// <see cref="AutoLadderFrames"/> entry above <paramref name="currentFrames"/>,
    /// or <see cref="CeilingFrames"/> once the ladder is exhausted.
    /// </summary>
    public static uint NextAutoRungUp(uint currentFrames)
    {
        foreach (var rung in AutoLadderFrames)
        {
            if (rung > currentFrames)
            {
                return rung;
            }
        }

        return CeilingFrames;
    }

    /// <summary>
    /// Next rung down on Auto's finer (~1.5x) ladder — the last
    /// <see cref="AutoLadderFrames"/> entry below <paramref name="currentFrames"/>,
    /// or <see cref="AutoFloorFrames"/> once the ladder's bottom is reached.
    /// </summary>
    public static uint NextAutoRungDown(uint currentFrames)
    {
        var result = AutoFloorFrames;
        foreach (var rung in AutoLadderFrames)
        {
            if (rung < currentFrames)
            {
                result = rung;
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private bool TryRaiseAuto(DateTimeOffset utcNow, bool bypassCooldown)
    {
        if (_effectiveFrames >= CeilingFrames)
        {
            return false;
        }

        if (!bypassCooldown &&
            _lastRaiseUtc != DateTimeOffset.MinValue &&
            utcNow - _lastRaiseUtc < AutoRaiseCoolDown)
        {
            return false;
        }

        var next = NextAutoRungUp(_effectiveFrames);
        if (next == _effectiveFrames)
        {
            return false;
        }

        // Decaying ratchet: raising back into a rung we were previously
        // forced to lower out of means that lower didn't stick. Lock that
        // rung against lowering again for a cooldown that grows with each
        // repeated re-raise, so the raise/lower/glitch loop can't repeat —
        // but the lock always eventually expires, unlike a permanent flag.
        if (_rungsLoweredFrom.Contains(next))
        {
            var reRaiseCount = _rungReRaiseCounts.TryGetValue(next, out var count)
                ? count + 1
                : 1;
            _rungReRaiseCounts[next] = reRaiseCount;

            var lockMinutes = Math.Min(
                RatchetBaseMinutes * Math.Pow(2, reRaiseCount - 1),
                RatchetMaxMinutes);
            _rungLowerLockedUntil[next] = utcNow + TimeSpan.FromMinutes(lockMinutes);
        }

        _effectiveFrames = next;
        _lastRaiseUtc = utcNow;
        return true;
    }

    private bool TryLowerAuto(DateTimeOffset utcNow)
    {
        if (_effectiveFrames <= AutoFloorFrames)
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
            utcNow - _lastLowerUtc < LowerCoolDown)
        {
            return false;
        }

        // Decaying ratchet: this rung was forced back into after a lower
        // didn't stick — hold here until the (growing but finite) cooldown
        // elapses rather than immediately repeating the same oscillation.
        if (_rungLowerLockedUntil.TryGetValue(_effectiveFrames, out var lockedUntil) &&
            utcNow < lockedUntil)
        {
            return false;
        }

        var next = NextAutoRungDown(_effectiveFrames);
        if (next >= _effectiveFrames)
        {
            return false;
        }

        _rungsLoweredFrom.Add(_effectiveFrames);
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
