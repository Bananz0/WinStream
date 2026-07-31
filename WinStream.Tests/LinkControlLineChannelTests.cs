using System.Text;
using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;

namespace WinStream.Tests;

public class LinkControlLineChannelTests
{
    [Fact]
    public async Task Reads_one_message_per_newline()
    {
        var channel = ReaderOver("HELLO\nPIN 1234\nSTOP\n");

        Assert.Equal(LinkControlVerb.Hello, (await channel.ReadAsync(default))!.Value.Verb);
        var pin = (await channel.ReadAsync(default))!.Value;
        Assert.Equal(LinkControlVerb.Pin, pin.Verb);
        Assert.Equal("1234", pin.Argument);
        Assert.Equal(LinkControlVerb.Stop, (await channel.ReadAsync(default))!.Value.Verb);
    }

    [Fact]
    public async Task Returns_null_at_end_of_stream()
    {
        var channel = ReaderOver("BYE\n");

        Assert.NotNull(await channel.ReadAsync(default));
        Assert.Null(await channel.ReadAsync(default));
    }

    [Fact]
    public async Task Yields_a_trailing_line_that_never_got_its_newline()
    {
        // A receiver that closes right after writing must not lose its last word.
        var channel = ReaderOver("OK");

        Assert.Equal(LinkControlVerb.Ok, (await channel.ReadAsync(default))!.Value.Verb);
        Assert.Null(await channel.ReadAsync(default));
    }

    [Fact]
    public async Task Tolerates_crlf_from_a_windows_peer()
    {
        var channel = ReaderOver("HELLO\r\n");

        Assert.Equal(LinkControlVerb.Hello, (await channel.ReadAsync(default))!.Value.Verb);
    }

    [Fact]
    public async Task Reassembles_a_line_split_across_reads()
    {
        var channel = new LinkControlLineChannel(new ChunkedStream("PI", "N 99", "88\n"));

        var message = (await channel.ReadAsync(default))!.Value;

        Assert.Equal(LinkControlVerb.Pin, message.Verb);
        Assert.Equal("9988", message.Argument);
    }

    [Fact]
    public async Task Refuses_a_line_past_the_ceiling()
    {
        // The listener is reachable from the whole LAN, so an unterminated line must
        // fail instead of growing the buffer.
        var channel = ReaderOver(new string('A', LinkControlLineChannel.MaxLineBytes + 1));

        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReadAsync(default));
    }

    [Fact]
    public async Task Writes_newline_terminated_frames()
    {
        var sink = new MemoryStream();
        var channel = new LinkControlLineChannel(sink);

        await channel.WriteAsync(LinkControlMessage.Hello, default);
        await channel.WriteAsync(
            LinkControlMessage.Start(47200, new AudioFormat(48000, 2, 16)),
            default);

        Assert.Equal(
            "HELLO\nSTART 47200 48000 2 16\n",
            Encoding.UTF8.GetString(sink.ToArray()));
    }

    [Fact]
    public async Task Round_trips_a_written_frame_back_through_the_reader()
    {
        var buffer = new MemoryStream();
        await new LinkControlLineChannel(buffer).WriteAsync(LinkControlMessage.Fail("bad pin"), default);
        buffer.Position = 0;

        var read = (await new LinkControlLineChannel(buffer).ReadAsync(default))!.Value;

        Assert.Equal(LinkControlVerb.Fail, read.Verb);
        Assert.Equal("bad pin", read.Argument);
    }

    private static LinkControlLineChannel ReaderOver(string text) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    /// <summary>Hands back one chunk per read so framing cannot rely on buffer luck.</summary>
    private sealed class ChunkedStream(params string[] chunks) : Stream
    {
        private readonly Queue<byte[]> _chunks =
            new(chunks.Select(Encoding.UTF8.GetBytes));

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            var chunk = _chunks.Dequeue();
            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
