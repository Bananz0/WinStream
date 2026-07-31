namespace WinStream.Core.Streaming;

/// <summary>
/// Bounds automatic connection retries. One transient failure must not disable
/// auto-connect for the rest of the session, but a receiver that keeps refusing
/// must not be retried on every discovery pass.
/// </summary>
public sealed class AutoConnectAttemptTracker
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _timeProvider;
    private int _failures;
    private DateTimeOffset? _retryNotBefore;
    private bool _connected;

    public AutoConnectAttemptTracker(
        int maxAttempts = 3,
        TimeSpan? cooldown = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(15);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool AttemptsAvailable =>
        !_connected &&
        _failures < _maxAttempts &&
        (_retryNotBefore is null || _timeProvider.GetUtcNow() >= _retryNotBefore.Value);

    public void RecordSuccess()
    {
        _connected = true;
        _retryNotBefore = null;
    }

    public void RecordFailure()
    {
        _failures++;
        _retryNotBefore = _timeProvider.GetUtcNow() + _cooldown;
    }

    /// <summary>Re-arms after the user toggles auto-connect or remembers a new receiver.</summary>
    public void Reset()
    {
        _failures = 0;
        _connected = false;
        _retryNotBefore = null;
    }
}
