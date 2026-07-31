using WinStream.Core.Protocol.AirPlay2;
using WinStream.Core.Streaming;
using WinStream.Network;
using WinStream.Streaming;

namespace WinStream.Tests;

public class AirPlay2SessionTests
{
    [Fact]
    public async Task Connect_with_gate_disabled_fails_clearly()
    {
        var receiver = new DeviceInfo
        {
            DisplayName = "Test Mac",
            IPAddress = "127.0.0.1",
            Port = 7000,
            DeviceID = "AA:BB:CC:DD:EE:FF"
        };

        await using var session = new AirPlay2Session(receiver, gateEnabled: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ConnectAsync());
        Assert.Contains("gated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SessionState.Failed, session.State);
    }

    [Fact]
    public void SubmitPcm_is_noop_when_not_streaming()
    {
        var receiver = new DeviceInfo
        {
            DisplayName = "Test",
            IPAddress = "127.0.0.1",
            Port = 7000
        };
        var session = new AirPlay2Session(receiver, gateEnabled: true);
        session.SubmitPcm(new byte[352 * 4], new WinStream.Core.Audio.AudioFormat(44100, 2, 16));
        Assert.Equal(SessionState.Disconnected, session.State);
    }
}

public class HkpPairSetupClientTests
{
    [Fact]
    public async Task PairAsync_maps_470_to_everyone_guidance()
    {
        var http = System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 470 Connection Authorization Required\r\nContent-Length: 0\r\n\r\n");
        await using var stream = new ScriptedDuplexStream(http);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HkpPairSetupClient.PairAsync(stream, "127.0.0.1", 7000));
        Assert.Contains("Everyone", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brew", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScriptedDuplexStream : Stream
    {
        private readonly byte[] _response;
        private int _readOffset;

        public ScriptedDuplexStream(byte[] response) => _response = response;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _response.Length - _readOffset;
            if (remaining <= 0)
            {
                return 0;
            }

            var n = Math.Min(count, remaining);
            Buffer.BlockCopy(_response, _readOffset, buffer, offset, n);
            _readOffset += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Discard request bytes; response is pre-scripted.
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
