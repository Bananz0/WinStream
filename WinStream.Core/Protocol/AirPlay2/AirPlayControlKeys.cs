using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>Control, event, and audio keys produced by transient or persistent HKP.</summary>
public sealed class AirPlayControlKeys : IDisposable
{
    private byte[]? _controlWriteKey;
    private byte[]? _controlReadKey;
    private byte[]? _eventsWriteKey;
    private byte[]? _eventsReadKey;
    private byte[]? _audioSharedKey;
    private bool _disposed;

    public AirPlayControlKeys(
        ReadOnlySpan<byte> controlWriteKey,
        ReadOnlySpan<byte> controlReadKey,
        ReadOnlySpan<byte> eventsWriteKey,
        ReadOnlySpan<byte> eventsReadKey,
        ReadOnlySpan<byte> audioSharedKey)
    {
        if (controlWriteKey.Length != 32 ||
            controlReadKey.Length != 32 ||
            eventsWriteKey.Length != 32 ||
            eventsReadKey.Length != 32)
        {
            throw new ArgumentException("Channel keys must be 32 bytes.");
        }

        if (audioSharedKey.Length < 32)
        {
            throw new ArgumentException("Audio shared key must be at least 32 bytes.");
        }

        _controlWriteKey = controlWriteKey.ToArray();
        _controlReadKey = controlReadKey.ToArray();
        _eventsWriteKey = eventsWriteKey.ToArray();
        _eventsReadKey = eventsReadKey.ToArray();
        _audioSharedKey = audioSharedKey[..32].ToArray();
    }

    /// <summary>Derives control + events keys from a pairing shared secret (SRP or X25519).</summary>
    public static AirPlayControlKeys FromSharedSecret(ReadOnlySpan<byte> sharedSecret)
    {
        var secret = sharedSecret.ToArray();
        try
        {
            return new AirPlayControlKeys(
                Hkdf(secret, "Control-Salt", "Control-Write-Encryption-Key", 32),
                Hkdf(secret, "Control-Salt", "Control-Read-Encryption-Key", 32),
                Hkdf(secret, "Events-Salt", "Events-Write-Encryption-Key", 32),
                Hkdf(secret, "Events-Salt", "Events-Read-Encryption-Key", 32),
                secret.AsSpan(0, Math.Min(32, secret.Length)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public ReadOnlySpan<byte> ControlWriteKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _controlWriteKey!;
        }
    }

    public ReadOnlySpan<byte> ControlReadKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _controlReadKey!;
        }
    }

    public ReadOnlySpan<byte> EventsWriteKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _eventsWriteKey!;
        }
    }

    public ReadOnlySpan<byte> EventsReadKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _eventsReadKey!;
        }
    }

    public byte[] AudioSharedKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var copy = new byte[32];
        _audioSharedKey!.CopyTo(copy, 0);
        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Zero(ref _controlWriteKey);
        Zero(ref _controlReadKey);
        Zero(ref _eventsWriteKey);
        Zero(ref _eventsReadKey);
        Zero(ref _audioSharedKey);
        _disposed = true;
    }

    private static void Zero(ref byte[]? buffer)
    {
        if (buffer is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(buffer);
        buffer = null;
    }

    private static byte[] Hkdf(byte[] ikm, string salt, string info, int length)
    {
        var generator = new HkdfBytesGenerator(new Sha512Digest());
        generator.Init(new HkdfParameters(
            ikm,
            Encoding.UTF8.GetBytes(salt),
            Encoding.UTF8.GetBytes(info)));
        var output = new byte[length];
        generator.GenerateBytes(output, 0, length);
        return output;
    }
}
