namespace WinStream.Core.Streaming;

/// <summary>
/// Bounds automatic connection retries. A <see cref="AutoConnectFailureKind.Config"/>
/// failure (wrong password, network ACL) cannot succeed without the user acting,
/// so it stays bounded and disables further retries until <see cref="Reset"/>.
/// Every other failure kind (<see cref="AutoConnectFailureKind.Silent"/>,
/// <see cref="AutoConnectFailureKind.Other"/>) retries unbounded on the plain
/// cooldown — a single transient or unconfirmed-cause failure must never
/// permanently strand the user from a receiver that would otherwise reconnect
/// fine on the next pass.
/// </summary>
public sealed class AutoConnectAttemptTracker
{
    private readonly int _maxConfigAttempts;
    private readonly TimeSpan _cooldown;
    private readonly TimeSpan _sessionLostCooldown;
    private readonly TimeProvider _timeProvider;
    private int _configFailures;
    private bool _configExhausted;
    private DateTimeOffset? _retryNotBefore;
    private bool _connected;

    public AutoConnectAttemptTracker(
        int maxConfigAttempts = 3,
        TimeSpan? cooldown = null,
        TimeSpan? sessionLostCooldown = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConfigAttempts, 1);
        _maxConfigAttempts = maxConfigAttempts;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(15);
        // A lost session already waited through the reconnect budget; a short pause is
        // enough to avoid a flap without making the user wait another full 15 seconds.
        _sessionLostCooldown = sessionLostCooldown ?? TimeSpan.FromSeconds(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool AttemptsAvailable =>
        !_connected &&
        !_configExhausted &&
        (_retryNotBefore is null || _timeProvider.GetUtcNow() >= _retryNotBefore.Value);

    public void RecordSuccess()
    {
        _connected = true;
        _retryNotBefore = null;
    }

    /// <summary>
    /// Records a failed attempt. Only <see cref="AutoConnectFailureKind.Config"/>
    /// counts toward the bounded cap; <see cref="AutoConnectFailureKind.Silent"/>
    /// and <see cref="AutoConnectFailureKind.Other"/> always leave
    /// <see cref="AttemptsAvailable"/> eligible again after the cooldown.
    /// </summary>
    public void RecordFailure(AutoConnectFailureKind kind = AutoConnectFailureKind.Other)
    {
        if (kind == AutoConnectFailureKind.Config)
        {
            _configFailures++;
            if (_configFailures >= _maxConfigAttempts)
            {
                _configExhausted = true;
            }
        }

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
        _configFailures = 0;
        _configExhausted = false;
        _retryNotBefore = _timeProvider.GetUtcNow() + _sessionLostCooldown;
    }

    /// <summary>Re-arms after the user toggles auto-connect or remembers a new receiver.</summary>
    public void Reset()
    {
        _configFailures = 0;
        _configExhausted = false;
        _connected = false;
        _retryNotBefore = null;
    }
}
