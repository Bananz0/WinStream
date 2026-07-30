using System.Security.Cryptography;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RaopCryptoTests
{
    [Fact]
    public void CreateEncryptionMaterial_EncryptsKeyForReceiver()
    {
        using var receiver = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(
            receiver.ExportSubjectPublicKeyInfo());

        var material = RaopCrypto.CreateEncryptionMaterial(publicKey);
        var encrypted = DecodeUnpaddedBase64(material.EncryptedAesKeyBase64);
        var decrypted = receiver.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);

        Assert.Equal(16, material.AesKey.Length);
        Assert.Equal(16, material.AesIv.Length);
        Assert.Equal(material.AesKey, decrypted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    public void ImportReceiverPublicKey_RejectsInvalidKeys(string encoded)
    {
        Assert.ThrowsAny<Exception>(() =>
            RaopCrypto.ImportReceiverPublicKey(encoded));
    }

    private static byte[] DecodeUnpaddedBase64(string value)
    {
        var padded = value.PadRight(
            value.Length + ((4 - value.Length % 4) % 4),
            '=');
        return Convert.FromBase64String(padded);
    }
}
