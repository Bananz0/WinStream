using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

public class EncryptedRtspTests
{
    [Fact]
    public void BinaryPlist_roundtrips_session_setup_dict()
    {
        var original = new Dictionary<string, object>
        {
            ["deviceID"] = "AA:BB:CC:DD:EE:FF",
            ["sessionUUID"] = "3195C737-1E6E-4487-BECB-4D287B7C7626",
            ["timingPort"] = 0L,
            ["timingProtocol"] = "NTP",
            ["groupContainsGroupLeader"] = false,
            ["qualifier"] = new List<object> { "txtAirPlay" }
        };

        var bytes = BinaryPlist.Write(original);
        Assert.StartsWith("bplist00", System.Text.Encoding.ASCII.GetString(bytes[..8]));

        var root = BinaryPlist.Read(bytes);
        Assert.True(BinaryPlist.TryGetInteger(root, "timingPort", out var timingPort));
        Assert.Equal(0, timingPort);

        var dict = Assert.IsType<Dictionary<string, object?>>(root);
        Assert.Equal("NTP", dict["timingProtocol"]);
        Assert.Equal(false, dict["groupContainsGroupLeader"]);
        var qualifier = Assert.IsType<object[]>(dict["qualifier"]);
        Assert.Equal("txtAirPlay", qualifier[0]);
    }

    [Fact]
    public void BinaryPlist_reads_eventPort_from_response_shape()
    {
        var response = BinaryPlist.Write(new Dictionary<string, object>
        {
            ["eventPort"] = 58168L,
            ["timingPort"] = 0L
        });

        var root = BinaryPlist.Read(response);
        Assert.True(BinaryPlist.TryGetInteger(root, "eventPort", out var eventPort));
        Assert.Equal(58168, eventPort);
    }

    [Fact]
    public async Task RtspCryptoStream_roundtrips_chunked_plaintext()
    {
        var writeKey = RandomNumberGenerator.GetBytes(32);
        var readKey = RandomNumberGenerator.GetBytes(32);
        // Client write == server read; invert for peer.
        await using var transport = new MemoryStream();
        using (var writer = new RtspCryptoStream(transport, writeKey, readKey))
        {
            var payload = System.Text.Encoding.ASCII.GetBytes(
                "RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Length: 0\r\n\r\n");
            // Force multi-chunk by writing >1024 would need larger; single chunk is enough.
            await writer.WritePlaintextAsync(payload);
        }

        transport.Position = 0;
        using var reader = new RtspCryptoStream(transport, readKey, writeKey);
        var chunk = await reader.ReadNextChunkAsync();
        Assert.Contains("RTSP/1.0 200 OK", System.Text.Encoding.ASCII.GetString(chunk));
    }

    [Fact]
    public void RtpChaChaEncryptor_appends_tag_and_nonce_trailer()
    {
        var shk = RandomNumberGenerator.GetBytes(32);
        var payload = "alac-frame"u8.ToArray();
        var encrypted = RtpChaChaEncryptor.EncryptPayload(
            shk,
            sequenceNumber: 42,
            rtpTimestamp: 44100,
            ssrc: 0x12345678,
            payload);

        Assert.Equal(payload.Length + 16 + 8, encrypted.Length);
        // Trailing nonce suffix starts with little-endian sequence 42.
        Assert.Equal(42, encrypted[^8]);
        Assert.Equal(0, encrypted[^7]);
    }

    [Fact]
    public void BinaryPlist_reads_real_values()
    {
        // Receivers report initialVolume as a real; hand-built because the writer
        // only emits the types the sender needs.
        var document = new List<byte>();
        document.AddRange("bplist00"u8.ToArray());
        document.AddRange([0xD1, 0x01, 0x02]);   // dict, 1 entry: key ref 1, value ref 2
        document.AddRange([0x51, (byte)'v']);    // ascii string "v"
        document.Add(0x23);                      // real, 8 bytes
        var real = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleBigEndian(real, -20.0);
        document.AddRange(real);

        var tableOffset = document.Count;
        document.AddRange([8, 11, 13]);          // offset table, 1 byte per offset
        document.AddRange(new byte[6]);
        document.Add(1);                         // offsetSize
        document.Add(1);                         // refSize
        AppendUInt64(document, 3);               // object count
        AppendUInt64(document, 0);               // top object
        AppendUInt64(document, (ulong)tableOffset);

        var root = Assert.IsType<Dictionary<string, object?>>(
            BinaryPlist.Read(document.ToArray()));
        Assert.Equal(-20.0, Assert.IsType<double>(root["v"]));

        static void AppendUInt64(List<byte> target, ulong value)
        {
            var buffer = new byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
            target.AddRange(buffer);
        }
    }

    [Fact]
    public void BinaryPlist_widens_refs_past_255_objects()
    {
        // 200 entries => 401 objects, so single-byte refs would wrap silently.
        var original = new Dictionary<string, object>();
        for (var i = 0; i < 200; i++)
        {
            original[$"key{i:D3}"] = $"value{i:D3}";
        }

        var root = Assert.IsType<Dictionary<string, object?>>(
            BinaryPlist.Read(BinaryPlist.Write(original)));

        Assert.Equal(200, root.Count);
        Assert.Equal("value000", root["key000"]);
        Assert.Equal("value199", root["key199"]);
    }

    [Fact]
    public void BinaryPlist_reads_stream_ports()
    {
        var response = BinaryPlist.Write(new Dictionary<string, object>
        {
            ["streams"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = 96L,
                    ["dataPort"] = 58169L,
                    ["controlPort"] = 58170L
                }
            }
        });

        var root = BinaryPlist.Read(response);
        Assert.True(BinaryPlist.TryGetStreamPorts(root, out var data, out var control));
        Assert.Equal(58169, data);
        Assert.Equal(58170, control);
    }

    [Fact]
    public void EventChannel_TryConsumeRequest_echoes_cseq_and_skips_body()
    {
        var pending = new System.Text.StringBuilder(
            "POST /command RTSP/1.0\r\nCSeq: 7\r\nContent-Length: 4\r\n\r\nabcd" +
            "POST /feedback RTSP/1.0\r\nCSeq: 8\r\n\r\n");

        Assert.True(EventChannel.TryConsumeRequest(pending, out var first));
        Assert.Equal("7", first);
        Assert.True(EventChannel.TryConsumeRequest(pending, out var second));
        Assert.Equal("8", second);
        Assert.False(EventChannel.TryConsumeRequest(pending, out _));
        Assert.Equal(0, pending.Length);
    }

    [Fact]
    public void SessionSetup_payload_advertises_ptp_and_clock_ports()
    {
        var payload = EncryptedRtspClient.BuildSessionSetupPayload(
            localAddress: "192.168.1.100",
            host: "192.168.1.10",
            deviceId: "AA:BB:CC:DD:EE:FF",
            sessionUuid: "SESSION");

        Assert.Equal("PTP", payload["timingProtocol"]);
        var peer = Assert.IsType<Dictionary<string, object>>(payload["timingPeerInfo"]);
        Assert.Equal("AA:BB:CC:DD:EE:FF", peer["ID"]);
        var ports = Assert.IsType<Dictionary<string, object>>(peer["ClockPorts"]);
        Assert.Equal((long)PtpClock.PortNumber, ports["192.168.1.100"]);
        Assert.Equal((long)PtpClock.PortNumber, ports["192.168.1.10"]);
        Assert.Equal(
            unchecked((long)PtpClock.ClockIdFromDeviceId("AA:BB:CC:DD:EE:FF")),
            peer["ClockID"]);
    }

    [Fact]
    public async Task EventChannel_DisposeAsync_is_idempotent_before_connect()
    {
        var channel = new EventChannel();
        await channel.DisposeAsync();
        await channel.DisposeAsync();
    }

    [Fact]
    public async Task EventChannel_connect_token_does_not_own_loop_and_dispose_stops_it()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var connectCancellation = new CancellationTokenSource();
        var accept = listener.AcceptTcpClientAsync();
        await using var channel = new EventChannel();
        var faulted = false;
        channel.Faulted += (_, _) => faulted = true;

        await channel.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            new byte[32],
            new byte[32],
            connectCancellation.Token);
        using var accepted = await accept;

        await connectCancellation.CancelAsync();
        await Task.Delay(25);
        Assert.False(faulted);

        await channel.DisposeAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.False(faulted);
    }

    [Fact]
    public async Task EventChannel_remote_close_raises_faulted()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        await using var channel = new EventChannel();
        var faulted = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Faulted += (_, error) => faulted.TrySetResult(error);

        await channel.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            new byte[32],
            new byte[32],
            CancellationToken.None);
        using (var accepted = await accept)
        {
            accepted.Close();
        }

        var error = await faulted.Task.WaitAsync(
            TimeSpan.FromSeconds(1));
        Assert.IsType<IOException>(error);
    }
}
