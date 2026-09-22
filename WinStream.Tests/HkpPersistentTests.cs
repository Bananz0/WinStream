using System.Text;
using WinStream.Core.Persistence;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

/// <summary>
/// Drives <see cref="HkpPersistent"/> over an in-memory duplex stream standing in
/// for the receiver, so the pairing failure paths are covered without a Mac.
/// </summary>
public class HkpPersistentTests
{
    private const string Host = "192.168.1.50";
    private const int Port = 7000;

    private static PairingCredentials Complete() => new()
    {
        ClientPairingId = "CLIENT",
        ClientSeedHex = new string('A', 64),
        AccessoryPairingId = "ACCESSORY",
        AccessoryPublicKeyHex = new string('C', 64)
    };

    private static Task<string?> Pin(CancellationToken _) => Task.FromResult<string?>("1234");

    private static Task<string?> NoPin(CancellationToken _) => Task.FromResult<string?>(null);

    private static byte[] HttpResponse(int status, byte[] body)
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} X\r\nContent-Length: {body.Length}\r\n\r\n");
        return [.. header, .. body];
    }

    private static byte[] Tlv(params (byte Type, byte[] Value)[] entries) => Tlv8.Encode(entries);

    [Fact]
    public async Task PairSetup_rejects_a_null_stream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HkpPersistent.PairSetupAsync(null!, Host, Port, Pin));
    }

    [Fact]
    public async Task RequestPinDisplay_posts_pair_pin_start()
    {
        using var stream = new ScriptedStream(HttpResponse(200, []));

        await HkpPersistent.RequestPinDisplayAsync(stream, Host, Port);

        var request = Encoding.ASCII.GetString(stream.Written);
        Assert.Contains("POST /pair-pin-start HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains($"X-Apple-HKP: {HkpPersistent.DefaultHkpType}", request, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestPinDisplay_allows_an_empty_body()
    {
        using var stream = new ScriptedStream(HttpResponse(200, []));

        await HkpPersistent.RequestPinDisplayAsync(stream, Host, Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PairSetup_rejects_a_blank_host(string host)
    {
        using var stream = new ScriptedStream();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HkpPersistent.PairSetupAsync(stream, host, Port, Pin));
    }

    [Fact]
    public async Task PairSetup_rejects_a_null_pin_prompt()
    {
        using var stream = new ScriptedStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, null!));
    }

    [Fact]
    public async Task Dismissing_the_pin_prompt_reports_a_skip_not_a_cancel()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Salt, new byte[16]),
            (Tlv8.PublicKey, new byte[384]))));

        await Assert.ThrowsAsync<PairingPinSkippedException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, NoPin));
    }

    [Fact]
    public async Task A_whitespace_pin_reports_a_skip()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Salt, new byte[16]),
            (Tlv8.PublicKey, new byte[384]))));

        await Assert.ThrowsAsync<PairingPinSkippedException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, _ => Task.FromResult<string?>("  ")));
    }

    [Fact]
    public async Task A_pairing_error_in_M2_is_reported_before_the_pin_prompt()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Error, [0x02]))));
        var prompted = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, _ =>
            {
                prompted = true;
                return Pin(default);
            }));

        Assert.Contains("authentication failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(prompted);
    }

    [Fact]
    public async Task Backoff_from_too_many_attempts_is_surfaced()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Error, [0x03]))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        Assert.Contains("backoff", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unexpected_state_is_rejected()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv((Tlv8.State, [0x04]))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        Assert.Contains("M2 state", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_http_470_is_explained_rather_than_shown_as_a_raw_code()
    {
        using var stream = new ScriptedStream(HttpResponse(470, []));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        Assert.DoesNotContain("HTTP 470", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_body_is_rejected()
    {
        using var stream = new ScriptedStream(HttpResponse(200, []));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));
    }

    [Fact]
    public async Task A_closed_connection_is_reported_as_io()
    {
        using var stream = new ScriptedStream();

        await Assert.ThrowsAsync<IOException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));
    }

    [Fact]
    public async Task PairVerify_rejects_incomplete_credentials()
    {
        using var stream = new ScriptedStream();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            HkpPersistent.PairVerifyAsync(
                stream,
                Host,
                Port,
                new PairingCredentials { ClientPairingId = "partial" }));
    }

    [Fact]
    public async Task PairVerify_rejects_null_credentials()
    {
        using var stream = new ScriptedStream();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            HkpPersistent.PairVerifyAsync(stream, Host, Port, null!));
    }

    [Fact]
    public async Task PairVerify_surfaces_an_error_tlv()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Error, [0x01]))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairVerifyAsync(stream, Host, Port, Complete()));
    }

    [Fact]
    public async Task PairVerify_requires_the_accessory_public_key()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv((Tlv8.State, [0x02]))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairVerifyAsync(stream, Host, Port, Complete()));
    }

    /// <summary>
    /// An M2 the SRP client will accept: any non-zero B below the RFC 5054 3072-bit
    /// modulus works, so a constant byte pattern keeps the fixture readable.
    /// </summary>
    private static byte[] UsableM2() => HttpResponse(200, Tlv(
        (Tlv8.State, [0x02]),
        (Tlv8.Salt, new byte[16]),
        (Tlv8.PublicKey, [.. Enumerable.Repeat((byte)0x02, 384)])));

    [Fact]
    public async Task A_wrong_code_is_reported_as_a_proof_mismatch_not_a_protocol_error()
    {
        // The receiver accepted M3 and answered M4, but its proof cannot match a
        // session key derived from the wrong PIN.
        using var stream = new ScriptedStream(
            UsableM2(),
            HttpResponse(200, Tlv(
                (Tlv8.State, [0x04]),
                (Tlv8.Proof, new byte[64]))));

        var ex = await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        Assert.Contains("AirPlay code", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_error_tlv_in_M4_is_surfaced_after_the_code_is_entered()
    {
        using var stream = new ScriptedStream(
            UsableM2(),
            HttpResponse(200, Tlv(
                (Tlv8.State, [0x04]),
                (Tlv8.Error, [0x02]))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        Assert.Contains("M4", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_M4_missing_its_proof_is_rejected()
    {
        using var stream = new ScriptedStream(
            UsableM2(),
            HttpResponse(200, Tlv((Tlv8.State, [0x04]))));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        Assert.Contains("Proof", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_sends_the_srp_public_key_and_proof_in_M3()
    {
        using var stream = new ScriptedStream(
            UsableM2(),
            HttpResponse(200, Tlv((Tlv8.State, [0x04]), (Tlv8.Proof, new byte[64]))));

        await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, Pin));

        var m3 = Tlv8.Decode(LastRequestBody(stream));
        Assert.Equal(new byte[] { 0x03 }, m3[Tlv8.State]);
        Assert.Equal(384, m3[Tlv8.PublicKey].Length);
        Assert.Equal(64, m3[Tlv8.Proof].Length);
    }

    [Fact]
    public async Task PairVerify_rejects_an_M2_whose_sealed_block_does_not_open()
    {
        // Only the real receiver holds the agreed key, so a forged EncryptedData must
        // fail the AEAD tag rather than yielding attacker-chosen identity fields.
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.PublicKey, [.. Enumerable.Repeat((byte)0x09, 32)]),
            (Tlv8.EncryptedData, new byte[48]))));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            HkpPersistent.PairVerifyAsync(stream, Host, Port, Complete()));
    }

    [Fact]
    public async Task PairVerify_rejects_an_accessory_public_key_of_the_wrong_length()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.PublicKey, new byte[16]),
            (Tlv8.EncryptedData, new byte[48]))));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            HkpPersistent.PairVerifyAsync(stream, Host, Port, Complete()));
    }

    [Fact]
    public async Task PairVerify_sends_a_32_byte_x25519_key_in_M1()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Error, [0x01]))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            HkpPersistent.PairVerifyAsync(stream, Host, Port, Complete()));

        var m1 = Tlv8.Decode(LastRequestBody(stream));
        Assert.Equal(new byte[] { 0x01 }, m1[Tlv8.State]);
        Assert.Equal(32, m1[Tlv8.PublicKey].Length);
    }

    /// <summary>Body of the last request written to the scripted receiver.</summary>
    private static byte[] LastRequestBody(ScriptedStream stream)
    {
        var written = stream.Written;
        var text = Encoding.ASCII.GetString(written);
        var start = text.LastIndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        return written[start..];
    }

    [Fact]
    public async Task Setup_sends_persistent_hkp_type_3_and_no_transient_flags()
    {
        using var stream = new ScriptedStream(HttpResponse(200, Tlv(
            (Tlv8.State, [0x02]),
            (Tlv8.Salt, new byte[16]),
            (Tlv8.PublicKey, new byte[384]))));

        await Assert.ThrowsAsync<PairingPinSkippedException>(() =>
            HkpPersistent.PairSetupAsync(stream, Host, Port, NoPin));

        var request = Encoding.ASCII.GetString(stream.Written);
        Assert.Contains("POST /pair-setup HTTP/1.1", request, StringComparison.Ordinal);
        Assert.Contains($"X-Apple-HKP: {HkpPersistent.DefaultHkpType}", request, StringComparison.Ordinal);

        var body = stream.Written[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        var m1 = Tlv8.Decode(body);
        Assert.Equal(new byte[] { 0x01 }, m1[Tlv8.State]);
        Assert.Equal(new byte[] { 0x00 }, m1[Tlv8.Method]);
        Assert.False(m1.ContainsKey(Tlv8.Flags));
    }

    /// <summary>Replays canned receiver bytes and records everything we send.</summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly MemoryStream _inbound;
        private readonly MemoryStream _outbound = new();

        public ScriptedStream(params byte[][] responses)
        {
            _inbound = new MemoryStream(responses.SelectMany(r => r).ToArray());
        }

        public byte[] Written => _outbound.ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inbound.Length;

        public override long Position
        {
            get => _inbound.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inbound.Read(buffer, offset, count);

        public override void Write(byte[] buffer, int offset, int count) =>
            _outbound.Write(buffer, offset, count);

        public override void Flush() => _outbound.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inbound.Dispose();
                _outbound.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
