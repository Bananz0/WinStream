using System.Security.Cryptography;

namespace WinStream.Core.Protocol.Raop;

public sealed record RaopEncryptionMaterial(
    byte[] AesKey,
    byte[] AesIv,
    string EncryptedAesKeyBase64,
    string AesIvBase64);

/// <summary>
/// Classic RAOP (et=1) encrypts the session AES key with Apple's well-known
/// AirTunes RSA public key — not the device <c>pk</c> TXT value. Modern receivers
/// advertise Ed25519 identity material in <c>pk</c>, which is unrelated to RSA.
/// </summary>
public static class RaopCrypto
{
    // AirPort Express / AirTunes RSA-2048 modulus published from iTunes (Johansen, 2004).
    private const string AirTunesModulusBase64 =
        "59dE8qLieItsH1WgjrcFRKj6eUWqi+bGLOX1HL3U3GhC/j0Qg90u3sG/1CUtwC" +
        "5vOYvfDmFI6oSFXi5ELabWJmT2dKHzBJKa3k9ok+8t9ucRqMd6DZHJ2YCCLlDR" +
        "KSKv6kDqnw4UwPdpOMXziC/AMj3Z/lUVX1G7WSHCAWKf1zNS1eLvqr+boEjXuB" +
        "OitnZ/bDzPHrTOZz0Dew0uowxf/+sG+NCK3eQJVxqcaJ/vEHKIVd2M+5qL71yJ" +
        "Q+87X6oV3eaYvt3zWZYD6z5vYTcrtij2VZ9Zmni/UAaHqn9JdsBWLUEpVviYnh" +
        "imNVvYFZeCXg/IdTQ+x4IRdiXNv5hEew==";

    private const string AirTunesExponentBase64 = "AQAB";

    public static RaopEncryptionMaterial CreateEncryptionMaterial()
    {
        using var rsa = ImportAirTunesPublicKey();
        return EncryptWith(rsa);
    }

    /// <summary>
    /// Optional override for receivers that publish a real RSA public key.
    /// Classic AirTunes senders normally call <see cref="CreateEncryptionMaterial"/>.
    /// </summary>
    public static RaopEncryptionMaterial CreateEncryptionMaterial(string receiverPublicKey)
    {
        using var rsa = ImportReceiverPublicKey(receiverPublicKey);
        return EncryptWith(rsa);
    }

    public static RSA ImportAirTunesPublicKey()
    {
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(AirTunesModulusBase64),
            Exponent = Convert.FromBase64String(AirTunesExponentBase64)
        });
        return rsa;
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

        // Modern AirPlay receivers publish Ed25519 (32 bytes) in pk — not RSA.
        if (bytes.Length is 32 or 64)
        {
            throw new CryptographicException(
                "The receiver pk is an identity key (Ed25519), not an RSA public key. " +
                "Classic RAOP uses the AirTunes RSA key instead.");
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

    private static RaopEncryptionMaterial EncryptWith(RSA rsa)
    {
        var key = RandomNumberGenerator.GetBytes(16);
        var iv = RandomNumberGenerator.GetBytes(16);
        var encryptedKey = rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA1);
        return new RaopEncryptionMaterial(
            key,
            iv,
            Convert.ToBase64String(encryptedKey).TrimEnd('='),
            Convert.ToBase64String(iv).TrimEnd('='));
    }
}
