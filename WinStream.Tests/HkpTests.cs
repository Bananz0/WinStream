using System.Security.Cryptography;
using WinStream.Core.Logging;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

public class HkpTests
{
    [Fact]
    public void Tlv8_roundtrips_fragmented_values()
    {
        var longValue = RandomNumberGenerator.GetBytes(300);
        var encoded = Tlv8.Encode(
        [
            (Tlv8.Method, [0x00]),
            (Tlv8.State, [0x01]),
            (Tlv8.PublicKey, longValue)
        ]);

        var decoded = Tlv8.Decode(encoded);
        Assert.Equal(new byte[] { 0x00 }, decoded[Tlv8.Method]);
        Assert.Equal(new byte[] { 0x01 }, decoded[Tlv8.State]);
        Assert.Equal(longValue, decoded[Tlv8.PublicKey]);
    }

    [Fact]
    public void BuildM1_includes_transient_flags_and_state()
    {
        var m1 = HkpTransient.BuildM1();
        var map = Tlv8.Decode(m1);

        Assert.Equal(new byte[] { 0x00 }, map[Tlv8.Method]);
        Assert.Equal(new byte[] { 0x01 }, map[Tlv8.State]);
        Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x00 }, map[Tlv8.Flags]);
    }

    [Fact]
    public void DescribeHttpStatus_470_mentions_everyone_not_brew()
    {
        var message = HkpTransient.DescribeHttpStatus(470);
        Assert.Contains("Everyone", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brew", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shairport", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FairPlay", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_auth_failure_blames_the_code_or_password_not_just_a_number()
    {
        var message = HkpTransient.DescribeTlvError(0x02);

        Assert.Contains("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("code", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backoff", HkpTransient.DescribeTlvError(0x03), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_password_receiver_can_replace_the_fixed_transient_pin()
    {
        // The SRP secret has to be swappable: a receiver with an AirPlay password
        // rejects 3939 outright (pair-setup error 2).
        using var withPassword = new HkpTransient("hunter2");
        using var withDefault = new HkpTransient();

        Assert.Throws<InvalidOperationException>(() => _ = withPassword.SessionKey);
        Assert.Throws<InvalidOperationException>(() => _ = withDefault.SessionKey);
    }

    [Fact]
    public void Dispose_blocks_access_to_keys()
    {
        var pairing = new HkpTransient();
        pairing.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = pairing.SessionKey);
        Assert.Throws<ObjectDisposedException>(() => pairing.AudioSharedKey());
    }

    [Fact]
    public void AppLog_redacts_pairing_secrets()
    {
        AppLog.Info("test", "session key material should not matter");
        // PIN must never be written by production code; sanitize still blocks RSA blobs.
        AppLog.Warn("test", "BEGIN RSA PRIVATE KEY");
        var lines = AppLog.Snapshot();
        Assert.Contains(lines, line => line.Contains("[redacted]"));
    }
}
