using System.Security.Cryptography;

namespace WinStream.Core.Protocol.Raop;

public sealed class AesAudioEncryptor : IDisposable
{
    private readonly byte[] _key;
    private readonly byte[] _iv;
    private readonly Aes _aes;

    public AesAudioEncryptor(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("AES key must be 16 bytes.", nameof(key));
        }

        if (iv.Length != 16)
        {
            throw new ArgumentException("AES IV must be 16 bytes.", nameof(iv));
        }

        _key = key.ToArray();
        _iv = iv.ToArray();
        _aes = Aes.Create();
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _aes.KeySize = 128;
        _aes.BlockSize = 128;
        _aes.Key = _key;
    }

    public int EncryptInPlace(Span<byte> payload)
    {
        var blockBytes = payload.Length - (payload.Length % 16);
        if (blockBytes == 0)
        {
            return payload.Length;
        }

        using var encryptor = _aes.CreateEncryptor(_key, _iv);
        var block = payload[..blockBytes].ToArray();
        var encrypted = encryptor.TransformFinalBlock(block, 0, block.Length);
        encrypted.CopyTo(payload);
        return payload.Length;
    }

    public void Dispose() => _aes.Dispose();
}
