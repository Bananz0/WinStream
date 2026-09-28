using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>ChaCha20-Poly1305 encryption for AirPlay 2 realtime RTP audio payloads.</summary>
public static class RtpChaChaEncryptor
{
    // The realtime send path calls EncryptPayload once per outgoing audio
    // packet (hundreds of times a second) with the same 32-byte session key
    // (shk) for the lifetime of a session. Re-creating a ChaCha20Poly1305
    // instance per packet was pure per-packet overhead, so each distinct key
    // gets one cached AEAD instance, keyed by the key's hex encoding.
    //
    // Multi-room fan-out means several AirPlay2 sessions can stream
    // concurrently, each with its own shk — a single process-wide cache slot
    // would let one session's key change dispose an instance another session
    // is still actively encrypting with. Entries here are never disposed or
    // evicted (the number of distinct keys touched over a process's lifetime
    // — bounded by session/reconnect count — stays small), which trades a
    // little unbounded-but-tiny cache growth for eliminating that race
    // entirely; ChaCha20Poly1305.Encrypt itself needs no external locking
    // for concurrent calls against the same instance with different nonces.
    private static readonly ConcurrentDictionary<string, ChaCha20Poly1305> AeadCache = new();

    private const int NonceLength = 12;
    private const int AadLength = 8;
    private const int TagLength = 16;
    private const int SequenceSuffixLength = 8;

    public static byte[] EncryptPayload(
        ReadOnlySpan<byte> shk,
        ushort sequenceNumber,
        uint rtpTimestamp,
        uint ssrc,
        ReadOnlySpan<byte> plaintext)
    {
        if (shk.Length != 32)
        {
            throw new ArgumentException("Audio shk must be 32 bytes.", nameof(shk));
        }

        Span<byte> nonce = stackalloc byte[NonceLength];
        BinaryPrimitives.WriteUInt16LittleEndian(nonce[4..], sequenceNumber);

        Span<byte> aad = stackalloc byte[AadLength];
        BinaryPrimitives.WriteUInt32BigEndian(aad[..4], rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(aad[4..], ssrc);

        // The only allocation left per packet: the returned buffer itself,
        // which the caller owns and sends on. Ciphertext and tag are written
        // directly into it instead of into separate scratch arrays.
        var result = new byte[plaintext.Length + TagLength + SequenceSuffixLength];
        var cipher = result.AsSpan(0, plaintext.Length);
        var tag = result.AsSpan(plaintext.Length, TagLength);

        var aead = GetOrCreateAead(shk);
        aead.Encrypt(nonce, plaintext, cipher, tag, aad);

        nonce.Slice(4, SequenceSuffixLength)
            .CopyTo(result.AsSpan(plaintext.Length + TagLength, SequenceSuffixLength));

        return result;
    }

    private static ChaCha20Poly1305 GetOrCreateAead(ReadOnlySpan<byte> shk) =>
        AeadCache.GetOrAdd(Convert.ToHexString(shk), static hex => new ChaCha20Poly1305(Convert.FromHexString(hex)));
}
