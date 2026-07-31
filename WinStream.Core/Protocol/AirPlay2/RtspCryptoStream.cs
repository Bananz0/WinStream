using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// ChaCha20-Poly1305 encrypted framing used on the AirPlay 2 control channel
/// after transient pair-setup: <c>[u16le len][cipher][16-byte tag]</c>.
/// </summary>
public sealed class RtspCryptoStream : IDisposable
{
    private const int TagLength = 16;
    private const int MaxChunk = 1024;

    private readonly Stream _inner;
    private readonly byte[] _writeKey;
    private readonly byte[] _readKey;
    private ulong _writeCounter;
    private ulong _readCounter;
    private bool _disposed;

    public RtspCryptoStream(Stream inner, ReadOnlySpan<byte> writeKey, ReadOnlySpan<byte> readKey)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (writeKey.Length != 32 || readKey.Length != 32)
        {
            throw new ArgumentException("Control keys must be 32 bytes.");
        }

        if (!ChaCha20Poly1305.IsSupported)
        {
            throw new PlatformNotSupportedException("ChaCha20Poly1305 is required for AirPlay 2.");
        }

        _inner = inner;
        _writeKey = writeKey.ToArray();
        _readKey = readKey.ToArray();
    }

    public async Task WritePlaintextAsync(
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var offset = 0;
        while (offset < plaintext.Length)
        {
            var chunkLen = Math.Min(MaxChunk, plaintext.Length - offset);
            var chunk = plaintext.Slice(offset, chunkLen);
            await WriteChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
            offset += chunkLen;
        }
    }

    public Task<byte[]> ReadNextChunkAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ReadChunkAsync(cancellationToken);
    }

    private async Task WriteChunkAsync(
        ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lengthBytes, (ushort)plaintext.Length);
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), _writeCounter++);

        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        using var aead = new ChaCha20Poly1305(_writeKey);
        aead.Encrypt(nonce, plaintext.Span, cipher, tag, lengthBytes);

        await _inner.WriteAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        await _inner.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
        await _inner.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> ReadChunkAsync(CancellationToken cancellationToken)
    {
        var lengthBytes = await ReadExactAsync(2, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        var cipher = await ReadExactAsync(length, cancellationToken).ConfigureAwait(false);
        var tag = await ReadExactAsync(TagLength, cancellationToken).ConfigureAwait(false);

        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), _readCounter++);
        var plain = new byte[length];
        using var aead = new ChaCha20Poly1305(_readKey);
        aead.Decrypt(nonce, cipher, tag, plain, lengthBytes);
        return plain;
    }

    private async Task<byte[]> ReadExactAsync(int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await _inner.ReadAsync(buffer.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Encrypted RTSP stream closed.");
            }

            offset += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_writeKey);
        CryptographicOperations.ZeroMemory(_readKey);
        _disposed = true;
    }
}
