using System.Security.Cryptography;

namespace WinStream.Core.Protocol.Raop;

public sealed record RaopEncryptionMaterial(
    byte[] AesKey,
    byte[] AesIv,
    string EncryptedAesKeyBase64,
    string AesIvBase64);

public static class RaopCrypto
{
    public static RaopEncryptionMaterial CreateEncryptionMaterial(string receiverPublicKey)
    {
        using var rsa = ImportReceiverPublicKey(receiverPublicKey);
        var key = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        var encryptedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA1);
        return new RaopEncryptionMaterial(
            key,
            iv,
            Convert.ToBase64String(encryptedKey).TrimEnd('='),
            Convert.ToBase64String(iv).TrimEnd('='));
    }

    public static RSA ImportReceiverPublicKey(string encodedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedKey);
        var normalized = encodedKey.Trim().Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - normalized.Length % 4) % 4),
            '=');

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(normalized);
        }
        catch (FormatException ex)
        {
            throw new FormatException("The receiver public key is not valid Base64.", ex);
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read == bytes.Length)
            {
                return rsa;
            }
        }
        catch (CryptographicException)
        {
            // Some RAOP receivers publish PKCS#1 RSAPublicKey bytes.
        }

        try
        {
            rsa.ImportRSAPublicKey(bytes, out var read);
            if (read == bytes.Length)
            {
                return rsa;
            }
        }
        catch (CryptographicException ex)
        {
            rsa.Dispose();
            throw new CryptographicException(
                "The receiver public key is not a supported RSA public key.",
                ex);
        }

        rsa.Dispose();
        throw new CryptographicException(
            "The receiver public key contains trailing or unsupported data.");
    }
}
