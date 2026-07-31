using System.Net;
using System.Net.Sockets;
using WinStream.Core.Audio;
using WinStream.Core.Protocol.Link;
using WinStream.Core.Streaming.Link;

namespace WinStream.Tests;

public class LinkSessionTests
{
    [Fact]
    public async Task SubmitPcm_sends_WSL1_packets_to_UDP_listener()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;

        await using var session = new LinkSession();
        await session.ConnectAsync(IPAddress.Loopback.ToString(), port);

        var format = new AudioFormat(
            Wsl1Constants.DefaultSampleRate,
            Wsl1Constants.DefaultChannels,
            16);
        var frameBytes = Wsl1Constants.DefaultPayloadBytes;
        var pcm = new byte[frameBytes];
        session.SubmitPcm(pcm, format, timestampTicks: 1234);

        listener.Client.ReceiveTimeout = 3000;
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var received = listener.Receive(ref remote);

        Assert.True(Wsl1Packet.TryRead(received, out var header, out var payload));
        Assert.Equal((ushort)0, header.Sequence);
        Assert.Equal(1234, header.TxQpcTicks);
        Assert.Equal(frameBytes, payload.Length);
        Assert.Equal(1, session.PacketsSent);
    }

    [Fact]
    public async Task SubmitPcm_is_noop_while_disconnected()
    {
        await using var session = new LinkSession();
        var format = new AudioFormat(48000, 2, 16);

        session.SubmitPcm(new byte[Wsl1Constants.DefaultPayloadBytes], format, 1);

        Assert.Equal(0, session.PacketsSent);
        Assert.Equal(LinkSessionState.Disconnected, session.State);
    }

    [Fact]
    public async Task SubmitPcm_increments_sequence_across_packets()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        await using var session = new LinkSession();
        await session.ConnectAsync(IPAddress.Loopback.ToString(), port);
        var format = new AudioFormat(48000, 2, 16);

        session.SubmitPcm(
            new byte[Wsl1Constants.DefaultPayloadBytes * 2],
            format,
            timestampTicks: 7);

        listener.Client.ReceiveTimeout = 3000;
        var remote = new IPEndPoint(IPAddress.Any, 0);
        var first = listener.Receive(ref remote);
        var second = listener.Receive(ref remote);
        Assert.True(Wsl1Packet.TryRead(first, out var firstHeader, out _));
        Assert.True(Wsl1Packet.TryRead(second, out var secondHeader, out _));
        Assert.Equal((ushort)0, firstHeader.Sequence);
        Assert.Equal((ushort)1, secondHeader.Sequence);
    }
}
