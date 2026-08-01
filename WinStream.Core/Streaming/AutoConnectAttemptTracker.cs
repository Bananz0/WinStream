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
    private readonly TimeSpan _sessionLostCooldown;
    private readonly TimeProvider _timeProvider;
    private int _failures;
    private DateTimeOffset? _retryNotBefore;
    private bool _connected;

    public AutoConnectAttemptTracker(
        int maxAttempts = 3,
        TimeSpan? cooldown = null,
        TimeSpan? sessionLostCooldown = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _maxAttempts = maxAttempts;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(15);
        // A lost session already waited through the reconnect budget; a short pause is
        // enough to avoid a flap without making the user wait another full 15 seconds.
        _sessionLostCooldown = sessionLostCooldown ?? TimeSpan.FromSeconds(5);
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

    /// <summary>
    /// A session WinStream did not end itself (receiver teardown, capture loss, an
    /// exhausted reconnect budget). The success latch has to lift or auto-connect
    /// stays disabled for the rest of the app's lifetime; the short cooldown keeps a
    /// flapping receiver from being retried on every discovery pass.
    /// </summary>
    public void RecordSessionLost()
    {
        _connected = false;
        _failures = 0;
        _retryNotBefore = _timeProvider.GetUtcNow() + _sessionLostCooldown;
    }

    /// <summary>Re-arms after the user toggles auto-connect or remembers a new receiver.</summary>
    public void Reset()
    {
        _failures = 0;
        _connected = false;
        _retryNotBefore = null;
    }
}
