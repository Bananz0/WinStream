using System.Security.Cryptography;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class AesAudioEncryptorTests
{
    [Fact]
    public void EncryptInPlace_encrypts_full_blocks_and_leaves_remainder()
    {
        var key = new byte[16];
        var iv = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            key[i] = (byte)i;
            iv[i] = (byte)(16 - i);
        }

        var payload = new byte[20];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(0xA0 + i);
        }

        var expectedRemainder = payload[16..].ToArray();
        byte[] expectedCipher;
        using (var aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;
            using var encryptor = aes.CreateEncryptor();
            expectedCipher = encryptor.TransformFinalBlock(payload, 0, 16);
        }

        using var sut = new AesAudioEncryptor(key, iv);
        sut.EncryptInPlace(payload);

        Assert.Equal(expectedCipher, payload[..16].ToArray());
        Assert.Equal(expectedRemainder, payload[16..].ToArray());
    }
}
