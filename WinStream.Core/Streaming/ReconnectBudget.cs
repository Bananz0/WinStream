namespace WinStream.Core.Streaming;

public sealed class ReconnectBudget
{
    private readonly TimeSpan _budget;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _deadline;

    public ReconnectBudget(
        TimeSpan? budget = null,
        TimeProvider? timeProvider = null)
    {
        _budget = budget ?? TimeSpan.FromSeconds(30);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsActive => _deadline is not null;

    public bool IsExpired =>
        _deadline is not null && _timeProvider.GetUtcNow() >= _deadline.Value;

    public TimeSpan? Remaining =>
        _deadline is null
            ? null
            : _deadline.Value - _timeProvider.GetUtcNow();

    public void Start()
    {
        _deadline = _timeProvider.GetUtcNow() + _budget;
    }

    public void Clear()
    {
        _deadline = null;
    }
}
