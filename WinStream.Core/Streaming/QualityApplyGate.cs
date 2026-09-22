namespace WinStream.Core.Streaming;

/// <summary>
/// Serializes preset changes against connect and disconnect.
/// </summary>
/// <remarks>
/// A preset change that lands while the session aggregate is busy must not be dropped:
/// the combo would keep showing a preset the live session never negotiated. The gate
/// remembers that a change is outstanding so the caller can replay it once the aggregate
/// frees up. Which preset to apply is never stored — the caller re-reads settings — so
/// several changes made during one rebuild collapse into a single replay.
/// </remarks>
public sealed class QualityApplyGate
{
    private bool _pending;

    /// <summary>True when a change is waiting for the aggregate to free up.</summary>
    public bool HasPending => _pending;

    /// <summary>
    /// Claims the right to run an apply pass. Returns false when <paramref name="aggregateBusy"/>
    /// is set, recording the change for a later <see cref="ShouldRepeat"/> or replay.
    /// </summary>
    public bool TryBegin(bool aggregateBusy)
    {
        if (aggregateBusy)
        {
            _pending = true;
            return false;
        }

        _pending = false;
        return true;
    }

    /// <summary>
    /// Call after an apply pass succeeds. True when another change arrived mid-pass and
    /// the caller should run one more; consumes the pending flag either way.
    /// </summary>
    public bool ShouldRepeat()
    {
        var repeat = _pending;
        _pending = false;
        return repeat;
    }

    /// <summary>
    /// Drops any outstanding change. Used after a failed apply, where replaying a preset
    /// the receiver just refused would spin the session tear-down loop.
    /// </summary>
    public void Clear() => _pending = false;
}
