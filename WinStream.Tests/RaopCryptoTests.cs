using System.Security.Cryptography;
using WinStream.Core.Protocol.Raop;

namespace WinStream.Tests;

public class RaopCryptoTests
{
    [Fact]
    public void CreateEncryptionMaterial_UsesAirTunesKey()
    {
        var material = RaopCrypto.CreateEncryptionMaterial();

        Assert.Equal(16, material.AesKey.Length);
        Assert.Equal(16, material.AesIv.Length);
        Assert.False(string.IsNullOrWhiteSpace(material.EncryptedAesKeyBase64));
        Assert.False(string.IsNullOrWhiteSpace(material.AesIvBase64));

        // RSA-2048 OAEP ciphertext is 256 bytes (unpadded base64 length varies).
        var encrypted = DecodeUnpaddedBase64(material.EncryptedAesKeyBase64);
        Assert.Equal(256, encrypted.Length);
    }

    [Fact]
    public void CreateEncryptionMaterial_EncryptsKeyForCustomReceiver()
    {
        using var receiver = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(
            receiver.ExportSubjectPublicKeyInfo());

        var material = RaopCrypto.CreateEncryptionMaterial(publicKey);
        var encrypted = DecodeUnpaddedBase64(material.EncryptedAesKeyBase64);
        var decrypted = receiver.Decrypt(encrypted, RSAEncryptionPadding.OaepSHA1);

        Assert.Equal(material.AesKey, decrypted);
    }

    [Fact]
    public void ImportAirTunesPublicKey_IsRsa2048()
    {
        using var rsa = RaopCrypto.ImportAirTunesPublicKey();
        Assert.Equal(2048, rsa.KeySize);
    }

    [Fact]
    public void ImportReceiverPublicKey_RejectsEd25519SizedPk()
    {
        var ed25519 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var error = Assert.Throws<CryptographicException>(() =>
            RaopCrypto.ImportReceiverPublicKey(ed25519));
        Assert.Contains("Ed25519", error.Message);
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
