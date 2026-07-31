using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;

namespace WinStream.Tests;

public class LinkControlServerTests
{
    private static readonly AudioFormat Format = new(48000, 2, 16);

    [Fact]
    public async Task Matching_pin_is_accepted_and_start_reaches_the_handler()
    {
        var handler = new RecordingHandler();

        await using var session = await ClientServerPair.StartAsync("4242", handler);
        var client = await session.ConnectClientAsync("4242");
        Assert.NotNull(client);
        await client!.StartAsync(47200, Format);
        await client.DisposeAsync();
        await session.CompleteAsync();

        Assert.Equal(new[] { (47200, 48000, 2, 16) }, handler.Starts);
    }

    [Fact]
    public async Task Wrong_pin_is_rejected_without_reaching_the_handler()
    {
        var handler = new RecordingHandler();

        await using var session = await ClientServerPair.StartAsync("4242", handler);
        var client = await session.ConnectClientAsync("0000");
        await session.CompleteAsync();

        Assert.Null(client);
        Assert.Empty(handler.Starts);
    }

    [Fact]
    public async Task A_pin_that_merely_starts_with_the_expected_value_is_rejected()
    {
        await using var session = await ClientServerPair.StartAsync("4242", new RecordingHandler());

        var client = await session.ConnectClientAsync("42420");
        await session.CompleteAsync();

        Assert.Null(client);
    }

    [Fact]
    public async Task Stop_reaches_the_handler_and_dispose_ends_the_session()
    {
        var handler = new RecordingHandler();

        await using var session = await ClientServerPair.StartAsync("4242", handler);
        var client = await session.ConnectClientAsync("4242");
        await client!.StartAsync(47200, Format);
        await client.StopAsync();
        await client.DisposeAsync();
        await session.CompleteAsync();

        // One explicit STOP, plus the implicit stop when the connection closes.
        Assert.Equal(2, handler.Stops);
    }

    [Fact]
    public async Task Telemetry_query_returns_the_receiver_counters()
    {
        var handler = new RecordingHandler
        {
            Telemetry = new LinkReceiverTelemetry(1, 2, 5, 900)
        };

        await using var session = await ClientServerPair.StartAsync("4242", handler);
        var client = await session.ConnectClientAsync("4242");
        var telemetry = await client!.QueryTelemetryAsync();
        await client.DisposeAsync();
        await session.CompleteAsync();

        Assert.Equal(handler.Telemetry, telemetry);
    }

    [Fact]
    public async Task Commands_before_authentication_are_refused()
    {
        var handler = new RecordingHandler();
        using var pair = DuplexPair.Create();
        var serve = LinkControlServer.ServeConnectionAsync(pair.Server, "4242", handler);

        await pair.WriteLineAsync("STAT");
        var reply = await pair.ReadLineAsync();
        await serve;

        Assert.Equal("FAIL expected HELLO", reply);
        Assert.Equal(0, handler.Stops);
    }

    [Fact]
    public async Task Re_authentication_attempts_are_refused_after_the_handshake()
    {
        using var pair = DuplexPair.Create();
        var serve = LinkControlServer.ServeConnectionAsync(pair.Server, "4242", new RecordingHandler());

        await pair.WriteLineAsync("HELLO");
        await pair.ReadLineAsync();
        await pair.WriteLineAsync("PIN 4242");
        await pair.ReadLineAsync();
        await pair.WriteLineAsync("PIN 4242");
        var reply = await pair.ReadLineAsync();
        pair.CloseClientWrites();
        await serve;

        Assert.Equal("FAIL already authenticated", reply);
    }

    [Fact]
    public async Task Unknown_verbs_are_refused_without_dropping_the_session()
    {
        using var pair = DuplexPair.Create();
        var serve = LinkControlServer.ServeConnectionAsync(pair.Server, "4242", new RecordingHandler());

        await pair.WriteLineAsync("HELLO");
        await pair.ReadLineAsync();
        await pair.WriteLineAsync("PIN 4242");
        await pair.ReadLineAsync();
        await pair.WriteLineAsync("SHUTDOWN");
        var refusal = await pair.ReadLineAsync();
        await pair.WriteLineAsync("STOP");
        var accepted = await pair.ReadLineAsync();
        pair.CloseClientWrites();
        await serve;

        Assert.Equal("FAIL unknown verb", refusal);
        Assert.Equal("OK", accepted);
    }

    [Fact]
    public async Task An_unterminated_line_cannot_grow_without_bound()
    {
        using var pair = DuplexPair.Create();
        var serve = LinkControlServer.ServeConnectionAsync(pair.Server, "4242", new RecordingHandler());

        await pair.WriteAsync(new string('A', LinkControlLineChannel.MaxLineBytes * 2));

        await Assert.ThrowsAsync<InvalidDataException>(() => serve);
    }

    private sealed class RecordingHandler : ILinkControlHandler
    {
        public List<(int Port, int SampleRate, int Channels, int Bits)> Starts { get; } = new();

        public int Stops { get; private set; }

        public LinkReceiverTelemetry Telemetry { get; init; }

        public Task OnStartAsync(int mediaPort, AudioFormat format, CancellationToken cancellationToken)
        {
            Starts.Add((mediaPort, format.SampleRate, format.Channels, format.BitsPerSample));
            return Task.CompletedTask;
        }

        public Task OnStopAsync(CancellationToken cancellationToken)
        {
            Stops++;
            return Task.CompletedTask;
        }

        public LinkReceiverTelemetry GetTelemetry() => Telemetry;
    }

    private sealed class ClientServerPair : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private Task _serve = Task.CompletedTask;

        private ClientServerPair(TcpListener listener) => _listener = listener;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<ClientServerPair> StartAsync(string pin, ILinkControlHandler handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var pair = new ClientServerPair(listener);
            pair._serve = LinkControlServer.ServeAsync(listener, pin, handler, pair._cts.Token);
            return Task.FromResult(pair);
        }

        public Task<LinkControlClient?> ConnectClientAsync(string pin) =>
            LinkControlClient.ConnectAsync(IPAddress.Loopback.ToString(), Port, pin);

        /// <summary>Lets the server finish the current connection before assertions run.</summary>
        public async Task CompleteAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _serve;
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await CompleteAsync();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    /// <summary>In-memory client/server streams so protocol tests need no sockets.</summary>
    private sealed class DuplexPair : IDisposable
    {
        private readonly AnonymousPipeServerStream _toServerWrite;
        private readonly AnonymousPipeClientStream _toServerRead;
        private readonly AnonymousPipeServerStream _toClientWrite;
        private readonly AnonymousPipeClientStream _toClientRead;

        private DuplexPair()
        {
            _toServerWrite = new AnonymousPipeServerStream(PipeDirection.Out);
            _toServerRead = new AnonymousPipeClientStream(
                PipeDirection.In,
                _toServerWrite.GetClientHandleAsString());
            _toClientWrite = new AnonymousPipeServerStream(PipeDirection.Out);
            _toClientRead = new AnonymousPipeClientStream(
                PipeDirection.In,
                _toClientWrite.GetClientHandleAsString());
            Server = new DuplexStream(_toServerRead, _toClientWrite);
        }

        public Stream Server { get; }

        public static DuplexPair Create() => new();

        public Task WriteLineAsync(string line) => WriteAsync(line + "\n");

        public async Task WriteAsync(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            await _toServerWrite.WriteAsync(bytes);
            await _toServerWrite.FlushAsync();
        }

        public async Task<string> ReadLineAsync()
        {
            var buffer = new byte[1];
            var line = new System.Text.StringBuilder();
            while (await _toClientRead.ReadAsync(buffer) == 1 && buffer[0] != (byte)'\n')
            {
                line.Append((char)buffer[0]);
            }

            return line.ToString();
        }

        public void CloseClientWrites() => _toServerWrite.Dispose();

        public void Dispose()
        {
            _toServerWrite.Dispose();
            _toServerRead.Dispose();
            _toClientWrite.Dispose();
            _toClientRead.Dispose();
        }
    }

    private sealed class DuplexStream : Stream
    {
        private readonly Stream _read;
        private readonly Stream _write;

        public DuplexStream(Stream read, Stream write)
        {
            _read = read;
            _write = write;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _write.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            _write.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            _read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            _write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _write.WriteAsync(buffer, cancellationToken);
    }
}
