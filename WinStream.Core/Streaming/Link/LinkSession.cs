using System.Buffers;
using System.Net;
using System.Net.Sockets;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.Link;

namespace WinStream.Core.Streaming.Link;

/// <summary>Minimal WSL1 UDP media sender for lab harness and future LinkOrchestrator.</summary>
public sealed class LinkSession : ILinkSession
{
    private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
    private readonly object _gate = new();
    private readonly byte[] _pending = new byte[Wsl1Constants.DefaultPayloadBytes * 4];
    private UdpClient? _udp;
    private IPEndPoint? _remote;
    private LinkSessionState _state = LinkSessionState.Disconnected;
    private ushort _sequence;
    private int _pendingBytes;
    private AudioFormat? _format;
    private long _packetsSent;

    public event EventHandler<LinkSessionStateChanged>? StateChanged;

    public LinkSessionState State => _state;

    public string RemoteHost { get; private set; } = string.Empty;

    public int MediaPort { get; private set; }

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    /// <summary>
    /// Opens the media socket only. Callers should go through
    /// <see cref="LinkConnectionCoordinator"/> instead: this skips the control-plane
    /// HELLO/PIN handshake, so calling it directly streams to a receiver that never
    /// authorized the sender.
    /// </summary>
    public Task ConnectAsync(
        string host,
        int mediaPort = Wsl1Constants.DefaultMediaPort,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        lock (_gate)
        {
            if (_state == LinkSessionState.Streaming)
            {
                return Task.CompletedTask;
            }

            DisconnectCore();

            _udp = new UdpClient();
            _remote = new IPEndPoint(IPAddress.Parse(host), mediaPort);
            RemoteHost = host;
            MediaPort = mediaPort;
            _pendingBytes = 0;
            _format = null;
            _sequence = 0;
            Transition(LinkSessionState.Streaming);
            AppLog.Info("link", $"Connected media UDP → {host}:{mediaPort}");
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DisconnectCore();
            Transition(LinkSessionState.Disconnected);
        }

        return Task.CompletedTask;
    }

    public void SubmitPcm(ReadOnlyMemory<byte> pcm, AudioFormat format, long timestampTicks)
    {
        if (_state != LinkSessionState.Streaming || _udp is null || _remote is null)
        {
            return;
        }

        if (format.BitsPerSample != 16 || format.Channels <= 0)
        {
            throw new ArgumentException("Link v1 expects PCM S16LE.", nameof(format));
        }

        var packetBytes = Wsl1Constants.DefaultPayloadBytes;
        var offset = 0;
        while (offset < pcm.Length)
        {
            lock (_gate)
            {
                if (_format is null)
                {
                    _format = format;
                }
                else if (!_format.Equals(format))
                {
                    throw new InvalidOperationException("Link session format changed mid-stream.");
                }

                var copied = CopyIntoPending(pcm.Span[offset..], packetBytes);
                offset += copied;
                while (_pendingBytes >= packetBytes)
                {
                    SendPacket(packetBytes, timestampTicks);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _udp?.Dispose();
        _udp = null;
    }

    private int CopyIntoPending(ReadOnlySpan<byte> source, int packetBytes)
    {
        var copied = 0;
        while (copied < source.Length && _pendingBytes < _pending.Length)
        {
            var chunk = Math.Min(source.Length - copied, _pending.Length - _pendingBytes);
            source.Slice(copied, chunk).CopyTo(_pending.AsSpan(_pendingBytes));
            _pendingBytes += chunk;
            copied += chunk;
        }

        return copied;
    }

    private void SendPacket(int packetBytes, long timestampTicks)
    {
        var rent = _pool.Rent(Wsl1Constants.DefaultPacketSize);
        try
        {
            var payload = _pending.AsSpan(0, packetBytes);
            var ticks = timestampTicks > 0 ? timestampTicks : System.Diagnostics.Stopwatch.GetTimestamp();
            var written = Wsl1Packet.Write(
                payload,
                _format!,
                _sequence++,
                ticks,
                flags: 0,
                rent);
            _ = _udp!.Send(rent.AsSpan(0, written), _remote!);
            Interlocked.Increment(ref _packetsSent);

            var remain = _pendingBytes - packetBytes;
            if (remain > 0)
            {
                Buffer.BlockCopy(_pending, packetBytes, _pending, 0, remain);
            }

            _pendingBytes = remain;
        }
        finally
        {
            _pool.Return(rent);
        }
    }

    private void DisconnectCore()
    {
        _udp?.Dispose();
        _udp = null;
        _remote = null;
        _pendingBytes = 0;
        _format = null;
    }

    private void Transition(LinkSessionState next, string? reason = null)
    {
        var prev = _state;
        if (prev == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, new LinkSessionStateChanged(prev, next, reason));
    }
}
