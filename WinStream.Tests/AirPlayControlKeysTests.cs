using System.Security.Cryptography;
using WinStream.Core.Protocol.AirPlay2;

namespace WinStream.Tests;

public class AirPlayControlKeysTests
{
    private static byte[] Filled(byte value, int length = 32)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static AirPlayControlKeys Create() =>
        new(Filled(1), Filled(2), Filled(3), Filled(4), Filled(5));

    [Fact]
    public void Exposes_each_channel_key_it_was_given()
    {
        using var keys = Create();

        Assert.Equal(Filled(1), keys.ControlWriteKey.ToArray());
        Assert.Equal(Filled(2), keys.ControlReadKey.ToArray());
        Assert.Equal(Filled(3), keys.EventsWriteKey.ToArray());
        Assert.Equal(Filled(4), keys.EventsReadKey.ToArray());
        Assert.Equal(Filled(5), keys.AudioSharedKey());
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    public void Rejects_channel_keys_that_are_not_32_bytes(int length)
    {
        Assert.Throws<ArgumentException>(() => new AirPlayControlKeys(
            Filled(1, length),
            Filled(2),
            Filled(3),
            Filled(4),
            Filled(5)));
    }

    [Fact]
    public void Rejects_a_short_audio_shared_key()
    {
        Assert.Throws<ArgumentException>(() => new AirPlayControlKeys(
            Filled(1),
            Filled(2),
            Filled(3),
            Filled(4),
            Filled(5, 16)));
    }

    [Fact]
    public void Truncates_a_longer_audio_shared_key_to_32_bytes()
    {
        using var keys = new AirPlayControlKeys(
            Filled(1),
            Filled(2),
            Filled(3),
            Filled(4),
            Filled(9, 64));

        Assert.Equal(32, keys.AudioSharedKey().Length);
    }

    [Fact]
    public void AudioSharedKey_hands_back_an_isolated_copy()
    {
        using var keys = Create();

        var first = keys.AudioSharedKey();
        CryptographicOperations.ZeroMemory(first);

        Assert.Equal(Filled(5), keys.AudioSharedKey());
    }

    [Fact]
    public void FromSharedSecret_is_deterministic_and_derives_distinct_keys()
    {
        var secret = Filled(7, 64);
        using var a = AirPlayControlKeys.FromSharedSecret(secret);
        using var b = AirPlayControlKeys.FromSharedSecret(secret);

        Assert.Equal(a.ControlWriteKey.ToArray(), b.ControlWriteKey.ToArray());
        Assert.Equal(a.EventsReadKey.ToArray(), b.EventsReadKey.ToArray());
        Assert.NotEqual(a.ControlWriteKey.ToArray(), a.ControlReadKey.ToArray());
        Assert.NotEqual(a.EventsWriteKey.ToArray(), a.EventsReadKey.ToArray());
        Assert.NotEqual(a.ControlWriteKey.ToArray(), a.EventsWriteKey.ToArray());
    }

    [Fact]
    public void FromSharedSecret_does_not_mutate_the_callers_secret()
    {
        var secret = Filled(7, 64);
        using var keys = AirPlayControlKeys.FromSharedSecret(secret);

        Assert.Equal(Filled(7, 64), secret);
    }

    [Fact]
    public void Access_after_dispose_throws()
    {
        var keys = Create();
        keys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => keys.AudioSharedKey());
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = keys.ControlWriteKey.Length;
        });
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var keys = Create();
        keys.Dispose();
        keys.Dispose();
    }
}
