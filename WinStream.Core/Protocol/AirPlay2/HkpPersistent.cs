using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Agreement.Srp;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities;
using WinStream.Core.Logging;
using WinStream.Core.Persistence;

namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Persistent HomeKit pairing: SRP pair-setup M1–M6 with an on-screen PIN, then
/// Ed25519/X25519 pair-verify. Stores a long-term identity on the receiver so
/// later connects can skip the macOS “Accept” prompt.
/// </summary>
public static class HkpPersistent
{
    public const int DefaultHkpType = 3;
    private const string SrpIdentity = "Pair-Setup";
    private const int SrpPublicKeyLength = 384;

    /// <summary>
    /// Asks the receiver to show its AirPlay code. Some Macs only display digits
    /// after this; pair-setup M1 alone is not enough.
    /// </summary>
    /// <remarks>
    /// Call on a disposable socket — some receivers close the connection after
    /// acknowledging. Persistent setup then continues on a fresh stream.
    /// </remarks>
    public static async Task RequestPinDisplayAsync(
        Stream stream,
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        await PostAsync(
                stream,
                host,
                port,
                "/pair-pin-start",
                Array.Empty<byte>(),
                cancellationToken,
                allowEmptyBody: true)
            .ConfigureAwait(false);
        AppLog.Info("pair", "pair-pin-start OK — receiver should show an AirPlay code");
    }

    /// <summary>
    /// Completes pair-setup M1–M6. With an on-screen code, call
    /// <see cref="RequestPinDisplayAsync"/> first so the receiver shows one. A
    /// password-protected receiver never shows a code: its AirPlay password is
    /// the SRP secret, and <paramref name="secretName"/> keeps errors truthful.
    /// </summary>
    public static async Task<PairingCredentials> PairSetupAsync(
        Stream stream,
        string host,
        int port,
        Func<CancellationToken, Task<string?>> requestPinAsync,
        CancellationToken cancellationToken = default,
        string secretName = "AirPlay code")
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(requestPinAsync);

        var m1 = Tlv8.Encode(
        [
            (Tlv8.State, [0x01]),
            (Tlv8.Method, [0x00])
        ]);
        var m2 = Tlv8.Decode(
            await PostAsync(stream, host, port, "/pair-setup", m1, cancellationToken)
                .ConfigureAwait(false));
        ThrowIfError(m2, "M2");
        RequireState(m2, 0x02, "M2");

        AppLog.Info("pair", $"Persistent pair-setup M2 — waiting for {secretName}");
        var pin = await requestPinAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pin))
        {
            throw new PairingPinSkippedException();
        }

        var salt = Tlv8.Require(m2, Tlv8.Salt, "Salt");
        var serverPublic = Tlv8.Require(m2, Tlv8.PublicKey, "PublicKey");
        var group = Srp6StandardGroups.rfc5054_3072;
        var client = new Srp6Client();
        client.Init(group, new Sha512Digest(), new SecureRandom());

        var identityBytes = Encoding.UTF8.GetBytes(SrpIdentity);
        var pinBytes = Encoding.UTF8.GetBytes(pin.Trim());
        BigInteger a;
        BigInteger s;
        try
        {
            a = client.GenerateClientCredentials(salt, identityBytes, pinBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pinBytes);
        }

        var b = new BigInteger(1, serverPublic);
        s = client.CalculateSecret(b);

        var aPad = Pad384(a);
        var bPad = Pad384(b);
        var sessionKey = Sha512(Pad384(s));
        var clientProof = HapClientProof(group.N, group.G, identityBytes, salt, aPad, bPad, sessionKey);

        var m3 = Tlv8.Encode(
        [
            (Tlv8.State, [0x03]),
            (Tlv8.PublicKey, aPad),
            (Tlv8.Proof, clientProof)
        ]);
        var m4 = Tlv8.Decode(
            await PostAsync(stream, host, port, "/pair-setup", m3, cancellationToken)
                .ConfigureAwait(false));
        ThrowIfError(m4, "M4");
        RequireState(m4, 0x04, "M4");

        var serverProof = Tlv8.Require(m4, Tlv8.Proof, "Proof");
        var expectedProof = Sha512(Concat(aPad, clientProof, sessionKey));
        if (!CryptographicOperations.FixedTimeEquals(expectedProof, serverProof))
        {
            throw new CryptographicException($"Pair-setup server proof mismatch — wrong {secretName}?");
        }

        var clientPairingId = Guid.NewGuid().ToString().ToUpperInvariant();
        var seed = RandomNumberGenerator.GetBytes(32);
        var clientPrivate = new Ed25519PrivateKeyParameters(seed, 0);
        var clientPublic = clientPrivate.GeneratePublicKey().GetEncoded();

        var clientX = Hkdf(sessionKey, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info", 32);
        var pairingIdBytes = Encoding.UTF8.GetBytes(clientPairingId);
        var clientSignature = Sign(clientPrivate, Concat(clientX, pairingIdBytes, clientPublic));

        var subTlv = Tlv8.Encode(
        [
            (Tlv8.Identifier, pairingIdBytes),
            (Tlv8.PublicKey, clientPublic),
            (Tlv8.Signature, clientSignature)
        ]);
        var encryptKey = Hkdf(sessionKey, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info", 32);
        var m5 = Tlv8.Encode(
        [
            (Tlv8.State, [0x05]),
            (Tlv8.EncryptedData, Seal(encryptKey, "PS-Msg05", subTlv))
        ]);
        var m6 = Tlv8.Decode(
            await PostAsync(stream, host, port, "/pair-setup", m5, cancellationToken)
                .ConfigureAwait(false));
        ThrowIfError(m6, "M6");
        RequireState(m6, 0x06, "M6");

        var accessorySub = Tlv8.Decode(
            Open(encryptKey, "PS-Msg06", Tlv8.Require(m6, Tlv8.EncryptedData, "EncryptedData")));
        var accessoryPairingId = Tlv8.Require(accessorySub, Tlv8.Identifier, "Identifier");
        var accessoryPublic = Tlv8.Require(accessorySub, Tlv8.PublicKey, "PublicKey");
        var accessorySignature = Tlv8.Require(accessorySub, Tlv8.Signature, "Signature");

        var accessoryX = Hkdf(sessionKey, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info", 32);
        if (!Verify(
                accessoryPublic,
                Concat(accessoryX, accessoryPairingId, accessoryPublic),
                accessorySignature))
        {
            throw new CryptographicException("Accessory signature verification failed.");
        }

        CryptographicOperations.ZeroMemory(sessionKey);
        CryptographicOperations.ZeroMemory(clientProof);
        CryptographicOperations.ZeroMemory(encryptKey);
        AppLog.Info("pair", "Persistent pair-setup complete");

        try
        {
            return new PairingCredentials
            {
                ClientPairingId = clientPairingId,
                ClientSeedHex = Convert.ToHexString(seed),
                AccessoryPairingId = Encoding.UTF8.GetString(accessoryPairingId),
                AccessoryPublicKeyHex = Convert.ToHexString(accessoryPublic)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Authenticates with a previously exchanged long-term identity.</summary>
    public static async Task<AirPlayControlKeys> PairVerifyAsync(
        Stream stream,
        string host,
        int port,
        PairingCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsComplete)
        {
            throw new ArgumentException("Incomplete pairing credentials.", nameof(credentials));
        }

        var generator = new X25519KeyPairGenerator();
        generator.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        var ourPrivate = (X25519PrivateKeyParameters)pair.Private;
        var ourPublic = ((X25519PublicKeyParameters)pair.Public).GetEncoded();

        var m1 = Tlv8.Encode(
        [
            (Tlv8.State, [0x01]),
            (Tlv8.PublicKey, ourPublic)
        ]);
        var m2 = Tlv8.Decode(
            await PostAsync(stream, host, port, "/pair-verify", m1, cancellationToken)
                .ConfigureAwait(false));
        ThrowIfError(m2, "verify M2");
        RequireState(m2, 0x02, "verify M2");

        var theirPublic = Tlv8.Require(m2, Tlv8.PublicKey, "PublicKey");
        var agreement = new X25519Agreement();
        agreement.Init(ourPrivate);
        var sharedSecret = new byte[agreement.AgreementSize];
        agreement.CalculateAgreement(new X25519PublicKeyParameters(theirPublic, 0), sharedSecret, 0);

        var sessionKey = Hkdf(sharedSecret, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info", 32);
        var theirSub = Tlv8.Decode(
            Open(sessionKey, "PV-Msg02", Tlv8.Require(m2, Tlv8.EncryptedData, "EncryptedData")));
        var theirPairingId = Tlv8.Require(theirSub, Tlv8.Identifier, "Identifier");
        var theirSignature = Tlv8.Require(theirSub, Tlv8.Signature, "Signature");

        var expectedId = Encoding.UTF8.GetBytes(credentials.AccessoryPairingId);
        if (!theirPairingId.AsSpan().SequenceEqual(expectedId))
        {
            throw new CryptographicException(
                $"Receiver identity changed: expected '{credentials.AccessoryPairingId}', " +
                $"got '{Encoding.UTF8.GetString(theirPairingId)}'.");
        }

        var accessoryPublic = Convert.FromHexString(credentials.AccessoryPublicKeyHex);
        if (!Verify(accessoryPublic, Concat(theirPublic, theirPairingId, ourPublic), theirSignature))
        {
            throw new CryptographicException("Receiver pair-verify signature failed.");
        }

        var seed = Convert.FromHexString(credentials.ClientSeedHex);
        byte[] ourSignature;
        var ourPairingId = Encoding.UTF8.GetBytes(credentials.ClientPairingId);
        try
        {
            var clientPrivate = new Ed25519PrivateKeyParameters(seed, 0);
            ourSignature = Sign(clientPrivate, Concat(ourPublic, ourPairingId, theirPublic));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        var m3 = Tlv8.Encode(
        [
            (Tlv8.State, [0x03]),
            (Tlv8.EncryptedData, Seal(
                sessionKey,
                "PV-Msg03",
                Tlv8.Encode(
                [
                    (Tlv8.Identifier, ourPairingId),
                    (Tlv8.Signature, ourSignature)
                ])))
        ]);
        var m4 = Tlv8.Decode(
            await PostAsync(stream, host, port, "/pair-verify", m3, cancellationToken)
                .ConfigureAwait(false));
        ThrowIfError(m4, "verify M4");
        RequireState(m4, 0x04, "verify M4");

        var keys = AirPlayControlKeys.FromSharedSecret(sharedSecret);
        CryptographicOperations.ZeroMemory(sessionKey);
        CryptographicOperations.ZeroMemory(sharedSecret);
        AppLog.Info("pair", "Persistent pair-verify OK");
        return keys;
    }

    private static byte[] Sign(Ed25519PrivateKeyParameters key, byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    private static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        verifier.BlockUpdate(data, 0, data.Length);
        return verifier.VerifySignature(signature);
    }

    private static byte[] Seal(byte[] key, string nonceLabel, byte[] plaintext)
    {
        using var aead = new ChaCha20Poly1305(key);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[16];
        aead.Encrypt(Nonce(nonceLabel), plaintext, cipher, tag);
        return Concat(cipher, tag);
    }

    private static byte[] Open(byte[] key, string nonceLabel, byte[] sealedData)
    {
        if (sealedData.Length < 16)
        {
            throw new CryptographicException("Encrypted TLV is shorter than its tag.");
        }

        using var aead = new ChaCha20Poly1305(key);
        var cipher = sealedData.AsSpan(0, sealedData.Length - 16);
        var tag = sealedData.AsSpan(sealedData.Length - 16);
        var plain = new byte[cipher.Length];
        aead.Decrypt(Nonce(nonceLabel), cipher, tag, plain);
        return plain;
    }

    private static byte[] Nonce(string label)
    {
        var nonce = new byte[12];
        Encoding.ASCII.GetBytes(label).CopyTo(nonce.AsSpan(4));
        return nonce;
    }

    private static void RequireState(IReadOnlyDictionary<byte, byte[]> map, byte expected, string step)
    {
        var state = Tlv8.Require(map, Tlv8.State, "State");
        if (state.Length != 1 || state[0] != expected)
        {
            throw new InvalidOperationException($"Expected {step} state {expected}, got {state[0]}.");
        }
    }

    private static void ThrowIfError(IReadOnlyDictionary<byte, byte[]> map, string step)
    {
        if (!map.TryGetValue(Tlv8.Error, out var error) || error.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{step} error: {HkpTransient.DescribeTlvError(error[0])}.");
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

    private static byte[] Hkdf(byte[] ikm, string salt, string info, int length)
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
        var buffer = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, buffer, offset, part.Length);
            offset += part.Length;
        }

        return buffer;
    }

    private static async Task<byte[]> PostAsync(
        Stream stream,
        string host,
        int port,
        string path,
        byte[] body,
        CancellationToken cancellationToken,
        bool allowEmptyBody = false)
    {
        var contentType = body.Length == 0
            ? "application/x-apple-binary-plist"
            : "application/pairing+tlv8";
        var header = Encoding.ASCII.GetBytes(
            $"POST {path} HTTP/1.1\r\n" +
            $"Host: {host}:{port}\r\n" +
            "User-Agent: AirPlay/415.3\r\n" +
            $"X-Apple-HKP: {DefaultHkpType}\r\n" +
            "X-Apple-Client-Name: WinStream\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: keep-alive\r\n" +
            "\r\n");
        var request = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, request, 0, header.Length);
        if (body.Length > 0)
        {
            Buffer.BlockCopy(body, 0, request, header.Length, body.Length);
        }

        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var (status, responseBody) = await ReadHttpResponseAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (status is 470 or 403)
        {
            throw new InvalidOperationException(HkpTransient.DescribeHttpStatus(status));
        }

        if (status is < 200 or >= 300)
        {
            throw new InvalidOperationException($"POST {path} returned HTTP {status}.");
        }

        if (responseBody.Length == 0 && !allowEmptyBody)
        {
            throw new InvalidOperationException($"POST {path} returned an empty body.");
        }

        return responseBody;
    }

    private static async Task<(int Status, byte[] Body)> ReadHttpResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(512);
        var single = new byte[1];
        while (headerBytes.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Receiver closed the pairing connection.");
            }

            headerBytes.Add(single[0]);
            var count = headerBytes.Count;
            if (count >= 4 &&
                headerBytes[count - 4] == '\r' &&
                headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' &&
                headerBytes[count - 1] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var parts = headerText.Split("\r\n", 2)[0].Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : -1;

        var contentLength = 0;
        foreach (var line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line["Content-Length:".Length..].Trim(), out var parsed))
            {
                contentLength = parsed;
            }
        }

        var body = new byte[contentLength];
        var offset = 0;
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Receiver closed the pairing body.");
            }

            offset += read;
        }

        return (status, body);
    }
}
