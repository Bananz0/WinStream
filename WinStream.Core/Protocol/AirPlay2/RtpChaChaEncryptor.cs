using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>ChaCha20-Poly1305 encryption for AirPlay 2 realtime RTP audio payloads.</summary>
public static class RtpChaChaEncryptor
{
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

        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(nonce.AsSpan(4), sequenceNumber);
        var aad = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(0), rtpTimestamp);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(4), ssrc);

        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aead = new ChaCha20Poly1305(shk);
        aead.Encrypt(nonce, plaintext, cipher, tag, aad);

        var result = new byte[cipher.Length + tag.Length + 8];
        Buffer.BlockCopy(cipher, 0, result, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, cipher.Length, tag.Length);
        Buffer.BlockCopy(nonce, 4, result, cipher.Length + tag.Length, 8);
        return result;
    }
}
