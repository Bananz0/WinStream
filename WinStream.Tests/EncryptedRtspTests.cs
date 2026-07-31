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
    public async Task RtspCryptoStream_rejects_tampered_tag()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var transport = new MemoryStream();
        using (var writer = new RtspCryptoStream(transport, key, key))
        {
            await writer.WritePlaintextAsync("hello"u8.ToArray());
        }

        var buffer = transport.ToArray();
        buffer[^1] ^= 0xFF;
        await using var tampered = new MemoryStream(buffer);
        using var reader = new RtspCryptoStream(tampered, key, key);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => reader.ReadNextChunkAsync());
    }
}
