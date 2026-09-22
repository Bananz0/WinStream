using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;

namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Answers the sender's control plane from a receiver's playout buffer, so every Link
/// RX reports the same counters without reimplementing the protocol.
/// </summary>
public sealed class LinkPlayoutControlHandler : ILinkControlHandler
{
    private readonly Action<int, AudioFormat>? _onStart;
    private readonly Action? _onStop;
    private LinkPlayoutBuffer? _buffer;

    public LinkPlayoutControlHandler(
        Action<int, AudioFormat>? onStart = null,
        Action? onStop = null)
    {
        _onStart = onStart;
        _onStop = onStop;
    }

    /// <summary>Set by the media loop once the buffer for this session exists.</summary>
    public LinkPlayoutBuffer? Buffer
    {
        get => Volatile.Read(ref _buffer);
        set => Volatile.Write(ref _buffer, value);
    }

    public Task OnStartAsync(int mediaPort, AudioFormat format, CancellationToken cancellationToken)
    {
        _onStart?.Invoke(mediaPort, format);
        return Task.CompletedTask;
    }

    public Task OnStopAsync(CancellationToken cancellationToken)
    {
        _onStop?.Invoke();
        return Task.CompletedTask;
    }

    public LinkReceiverTelemetry GetTelemetry()
    {
        var buffer = Buffer;
        return buffer is null
            ? default
            : new LinkReceiverTelemetry(
                buffer.Underruns,
                buffer.LateOrLostPackets,
                buffer.TargetMilliseconds,
                buffer.PacketsAccepted);
    }
}
