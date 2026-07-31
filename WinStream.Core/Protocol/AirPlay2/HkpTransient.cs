using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Transient HomeKit pair-setup (X-Apple-HKP: 4). PIN 3939, SRP-6a-3072 / SHA-512.
/// Proofs follow HAP (not BouncyCastle's RFC-5054 evidence helpers).
/// </summary>
public sealed class HkpTransient : IDisposable
{
    public const string TransientPin = "3939";
    public const string IdentityName = "Pair-Setup";
    public const int SrpPublicKeyLength = 384;
    public const uint TransientFlags = 0x00000010;

    private static readonly byte[] IdentityBytes = Encoding.UTF8.GetBytes(IdentityName);
    private static readonly byte[] PinBytes = Encoding.UTF8.GetBytes(TransientPin);

    private byte[]? _sessionKey;
    private byte[]? _controlWriteKey;
    private byte[]? _controlReadKey;
    private byte[]? _eventsWriteKey;
    private byte[]? _eventsReadKey;
    private bool _disposed;

    public IReadOnlyList<byte> SessionKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sessionKey ?? throw new InvalidOperationException("Pairing is not complete.");
        }
    }

    public IReadOnlyList<byte> ControlWriteKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _controlWriteKey ?? throw new InvalidOperationException("Pairing is not complete.");
        }
    }

    public IReadOnlyList<byte> ControlReadKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _controlReadKey ?? throw new InvalidOperationException("Pairing is not complete.");
        }
    }

    public IReadOnlyList<byte> EventsWriteKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _eventsWriteKey ?? throw new InvalidOperationException("Pairing is not complete.");
        }
    }

    public IReadOnlyList<byte> EventsReadKey
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _eventsReadKey ?? throw new InvalidOperationException("Pairing is not complete.");
        }
    }

    /// <summary>First 32 bytes of the 64-byte SRP session key — AP2 realtime audio <c>shk</c>.</summary>
    public byte[] AudioSharedKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionKey is null)
        {
            throw new InvalidOperationException("Pairing is not complete.");
        }

        var audio = new byte[32];
        _sessionKey.AsSpan(0, 32).CopyTo(audio);
        return audio;
    }

    public static byte[] BuildM1()
    {
        var flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, TransientFlags);
        return Tlv8.Encode(
        [
            (Tlv8.Method, [0x00]),
            (Tlv8.State, [0x01]),
            (Tlv8.Flags, flags)
        ]);
    }

    public byte[] ProcessM2AndBuildM3(ReadOnlySpan<byte> m2Tlv)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var map = Tlv8.Decode(m2Tlv);
        ThrowIfError(map);

        var state = Tlv8.Require(map, Tlv8.State, "State");
        if (state is not [0x02])
        {
            throw new InvalidOperationException($"Expected pair-setup state M2, got {state[0]}.");
        }

        var salt = Tlv8.Require(map, Tlv8.Salt, "Salt");
        var serverPublic = Tlv8.Require(map, Tlv8.PublicKey, "PublicKey");
        if (serverPublic.Length > SrpPublicKeyLength)
        {
            throw new InvalidOperationException("Server SRP public key is too large.");
        }

        var group = Srp6StandardGroups.rfc5054_3072;
        var digest = new Sha512Digest();
        var client = new Srp6Client();
        client.Init(group, digest, new SecureRandom());

        var A = client.GenerateClientCredentials(salt, IdentityBytes, PinBytes);
        var B = new BigInteger(1, serverPublic);
        var S = client.CalculateSecret(B);

        var aPad = Pad384(A);
        var bPad = Pad384(B);
        var sPad = Pad384(S);
        var sessionKey = Sha512(sPad);
        var clientProof = HapClientProof(group.N, group.G, IdentityBytes, salt, aPad, bPad, sessionKey);

        _sessionKey = sessionKey;
        // Server proof verification happens in CompleteWithM4; stash client proof there via field.
        _pendingA = aPad;
        _pendingM1 = clientProof;

        return Tlv8.Encode(
        [
            (Tlv8.PublicKey, aPad),
            (Tlv8.Proof, clientProof),
            (Tlv8.State, [0x03])
        ]);
    }

    private byte[]? _pendingA;
    private byte[]? _pendingM1;

    public void CompleteWithM4(ReadOnlySpan<byte> m4Tlv)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sessionKey is null || _pendingA is null || _pendingM1 is null)
        {
            throw new InvalidOperationException("Call ProcessM2AndBuildM3 before CompleteWithM4.");
        }

        var map = Tlv8.Decode(m4Tlv);
        ThrowIfError(map);

        var state = Tlv8.Require(map, Tlv8.State, "State");
        if (state is not [0x04])
        {
            throw new InvalidOperationException($"Expected pair-setup state M4, got {state[0]}.");
        }

        var serverProof = Tlv8.Require(map, Tlv8.Proof, "Proof");
        var expected = Sha512(Concat(_pendingA, _pendingM1, _sessionKey));
        if (!CryptographicOperations.FixedTimeEquals(expected, serverProof))
        {
            throw new CryptographicException("Pair-setup server proof verification failed.");
        }

        _controlWriteKey = HkdfSha512(
            _sessionKey,
            "Control-Salt",
            "Control-Write-Encryption-Key",
            32);
        _controlReadKey = HkdfSha512(
            _sessionKey,
            "Control-Salt",
            "Control-Read-Encryption-Key",
            32);
        _eventsWriteKey = HkdfSha512(
            _sessionKey,
            "Events-Salt",
            "Events-Write-Encryption-Key",
            32);
        _eventsReadKey = HkdfSha512(
            _sessionKey,
            "Events-Salt",
            "Events-Read-Encryption-Key",
            32);

        CryptographicOperations.ZeroMemory(_pendingM1);
        _pendingA = null;
        _pendingM1 = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_sessionKey is not null)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
        }

        if (_controlWriteKey is not null)
        {
            CryptographicOperations.ZeroMemory(_controlWriteKey);
        }

        if (_controlReadKey is not null)
        {
            CryptographicOperations.ZeroMemory(_controlReadKey);
        }

        if (_eventsWriteKey is not null)
        {
            CryptographicOperations.ZeroMemory(_eventsWriteKey);
        }

        if (_eventsReadKey is not null)
        {
            CryptographicOperations.ZeroMemory(_eventsReadKey);
        }

        if (_pendingM1 is not null)
        {
            CryptographicOperations.ZeroMemory(_pendingM1);
        }

        _sessionKey = null;
        _controlWriteKey = null;
        _controlReadKey = null;
        _eventsWriteKey = null;
        _eventsReadKey = null;
        _pendingA = null;
        _pendingM1 = null;
        _disposed = true;
    }

    public static string DescribeHttpStatus(int status) =>
        status switch
        {
            470 =>
                "Pairing refused (470). On the Mac, set AirPlay Receiver to allow Everyone " +
                "(or anyone on the same network) and disable a required password.",
            403 =>
                "Receiver returned 403 Forbidden. Confirm AirPlay Receiver is enabled and " +
                "allowed for Everyone / same network.",
            _ => $"Pairing HTTP status {status}."
        };

    private static void ThrowIfError(IReadOnlyDictionary<byte, byte[]> map)
    {
        if (!map.TryGetValue(Tlv8.Error, out var error) || error.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException($"Pair-setup error TLV code {error[0]}.");
    }

    private static byte[] HapClientProof(
        BigInteger n,
        BigInteger g,
        byte[] identity,
        byte[] salt,
        byte[] aPad,
        byte[] bPad,
        byte[] sessionKey)
    {
        var hN = Sha512(Pad384(n));
        // HAP uses minimal g bytes (0x05), not padded to 384.
        var hG = Sha512(g.ToByteArrayUnsigned());
        var xor = new byte[64];
        for (var i = 0; i < 64; i++)
        {
            xor[i] = (byte)(hN[i] ^ hG[i]);
        }

        return Sha512(Concat(xor, Sha512(identity), salt, aPad, bPad, sessionKey));
    }

    private static byte[] Pad384(BigInteger value) =>
        BigIntegers.AsUnsignedByteArray(SrpPublicKeyLength, value);

    private static byte[] Sha512(byte[] data)
    {
        var digest = new Sha512Digest();
        digest.BlockUpdate(data, 0, data.Length);
        var output = new byte[64];
        digest.DoFinal(output, 0);
        return output;
    }

    private static byte[] HkdfSha512(byte[] ikm, string salt, string info, int length)
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

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }

        return buffer;
    }
}
