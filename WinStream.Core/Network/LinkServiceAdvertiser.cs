using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.Link;

namespace WinStream.Core.Network;

/// <summary>
/// Advertises one Link receiver over mDNS so senders can find it without typing an IP.
/// Scoped to answering our own service: it is not a general-purpose responder, and it
/// never touches the AirPlay browse path.
/// </summary>
public sealed class LinkServiceAdvertiser : IAsyncDisposable
{
    private const int MdnsPort = 5353;
    private static readonly IPAddress MulticastGroup = IPAddress.Parse("224.0.0.251");
    private static readonly IPEndPoint MulticastEndpoint = new(MulticastGroup, MdnsPort);

    private readonly UdpClient _socket;
    private readonly LinkServiceRecordSet _records;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listener;
    private bool _disposed;

    private LinkServiceAdvertiser(UdpClient socket, LinkServiceRecordSet records)
    {
        _socket = socket;
        _records = records;
        _listener = Task.Run(() => ListenAsync(_cts.Token));
    }

    /// <param name="instanceLabel">Shown in the sender's device list.</param>
    /// <param name="address">IPv4 the sender should stream to; discovered when null.</param>
    public static LinkServiceAdvertiser Start(
        string instanceLabel,
        int mediaPort = Wsl1Constants.DefaultMediaPort,
        IPAddress? address = null,
        string? hostLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceLabel);
        ArgumentOutOfRangeException.ThrowIfLessThan(mediaPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mediaPort, ushort.MaxValue);

        var local = address ?? ResolveLocalIPv4()
            ?? throw new InvalidOperationException("No usable IPv4 address to advertise.");
        var records = new LinkServiceRecordSet(
            instanceLabel,
            hostLabel ?? Dns.GetHostName(),
            local,
            (ushort)mediaPort,
            new[]
            {
                new KeyValuePair<string, string>("name", instanceLabel),
                new KeyValuePair<string, string>("ver", Wsl1Constants.Version.ToString()),
                new KeyValuePair<string, string>("fmt", "pcm16"),
                new KeyValuePair<string, string>("rate", Wsl1Constants.DefaultSampleRate.ToString())
            });

        var socket = CreateSocket(local);
        var advertiser = new LinkServiceAdvertiser(socket, records);
        advertiser.Announce();
        AppLog.Info("link", $"Advertising {records.InstanceName} at {local}:{mediaPort}");
        return advertiser;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SendGoodbye();
        await _cts.CancelAsync().ConfigureAwait(false);
        _socket.Dispose();
        try
        {
            await _listener.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Expected: cancelling the listener races the socket close.
        }

        _cts.Dispose();
    }

    private static UdpClient CreateSocket(IPAddress local)
    {
        var socket = new UdpClient();
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
        try
        {
            socket.JoinMulticastGroup(MulticastGroup, local);
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new InvalidOperationException(
                $"Could not join the mDNS group on {local}: {ex.SocketErrorCode}.", ex);
        }

        socket.MulticastLoopback = true;
        return socket;
    }

    private static IPAddress? ResolveLocalIPv4() =>
        MulticastAdapters.Usable()
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(candidate =>
                candidate.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(candidate));

    private void Announce() => Send(_records.Announcement(), Array.Empty<DnsResourceRecord>());

    private void SendGoodbye()
    {
        try
        {
            // TTL 0 retires the records immediately instead of leaving a ghost for 120 s.
            Send(_records.Announcement(ttlSeconds: 0), Array.Empty<DnsResourceRecord>());
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            AppLog.Info("link", $"mDNS goodbye not sent: {ex.GetType().Name}");
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                AppLog.Info("link", $"mDNS receive failed: {ex.SocketErrorCode}");
                continue;
            }

            try
            {
                Respond(received.Buffer);
            }
            catch (Exception ex)
            {
                // A malformed or hostile packet must never take the receiver down.
                AppLog.Info("link", $"mDNS query ignored: {ex.GetType().Name}");
            }
        }
    }

    private void Respond(byte[] query)
    {
        if (!MdnsWire.TryReadQuestions(query, out var questions) ||
            !_records.TryAnswer(questions, out var answers, out var additional))
        {
            return;
        }

        Send(answers, additional);
    }

    private void Send(
        IReadOnlyList<DnsResourceRecord> answers,
        IReadOnlyList<DnsResourceRecord> additional)
    {
        var buffer = new byte[MdnsWire.MaxMessageBytes];
        var written = MdnsWire.WriteResponse(buffer, answers, additional);
        _socket.Send(buffer.AsSpan(0, written), MulticastEndpoint);
    }
}
