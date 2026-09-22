using WinStream.Core.Logging;

namespace WinStream.Core.Streaming;

/// <summary>
/// Joins an in-flight prompt for the same receiver instead of racing it. Only one
/// UI dialog slot exists, but answers must not bleed across different receivers.
/// </summary>
public sealed class SingleFlightPrompt
{
    private readonly object _gate = new();
    private string? _inFlightKey;
    private Task<string> _inFlight = Task.FromResult(string.Empty);

    /// <summary>
    /// Returns the in-flight prompt when <paramref name="flightKey"/> matches;
    /// otherwise waits for the current prompt to finish and starts a new one.
    /// </summary>
    public Task<string> JoinOrStart(string flightKey, string category, Func<Task<string>> start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flightKey);
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(start);

        Task<string>? waitFor;
        lock (_gate)
        {
            if (!_inFlight.IsCompleted)
            {
                if (string.Equals(_inFlightKey, flightKey, StringComparison.OrdinalIgnoreCase))
                {
                    AppLog.Info(category, $"{category} prompt already open; joining it.");
                    return _inFlight;
                }

                waitFor = _inFlight;
            }
            else
            {
                return StartLocked(flightKey, category, start);
            }
        }

        return WaitThenJoinAsync(waitFor!, flightKey, category, start);
    }

    private async Task<string> WaitThenJoinAsync(
        Task<string> waitFor,
        string flightKey,
        string category,
        Func<Task<string>> start)
    {
        try
        {
            await waitFor.ConfigureAwait(false);
        }
        catch
        {
            // A faulted prompt still frees the slot for the next caller.
        }

        return await JoinOrStart(flightKey, category, start).ConfigureAwait(false);
    }

    private Task<string> StartLocked(string flightKey, string category, Func<Task<string>> start)
    {
        _inFlightKey = flightKey;
        var prompt = start();
        _inFlight = prompt.ContinueWith(
            task =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_inFlight, task))
                    {
                        _inFlightKey = null;
                    }
                }

                return task.GetAwaiter().GetResult();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return _inFlight;
    }
}
