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
}
