using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinStream.Core.Protocol.Raop;

/// <summary>
/// RTSP Digest access authentication for AirPlay Receiver passwords (RFC 2617).
/// </summary>
/// <remarks>
/// OwnTone's AirPlay 2 path uses an empty username; classic RAOP uses
/// <c>iTunes</c>. Realm and nonce come from the 401 challenge.
/// </remarks>
public static partial class RtspDigestAuth
{
    /// <summary>
    /// Builds an <c>Authorization</c> header value for a Digest challenge using
    /// the realm's own username policy, or <c>null</c> when the challenge is
    /// missing required fields.
    /// </summary>
    public static string? TryBuildAuthorization(
        string? challenge,
        string method,
        string uri,
        string password)
    {
        if (!TryParseChallenge(challenge, out var realm, out var nonce))
        {
            return null;
        }

        return BuildAuthorization(realm, nonce, method, uri, password, UsernameCandidates(realm)[0]);
    }

    public static string BuildAuthorization(
        string realm,
        string nonce,
        string method,
        string uri,
        string password,
        string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(uri);
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        var ha1 = Md5Hex($"{username}:{realm}:{password}");
        var ha2 = Md5Hex($"{method}:{uri}");
        var response = Md5Hex($"{ha1}:{nonce}:{ha2}");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Digest username=\"{EscapeDigestValue(username)}\", realm=\"{EscapeDigestValue(realm)}\", nonce=\"{EscapeDigestValue(nonce)}\", uri=\"{EscapeDigestValue(uri)}\", response=\"{response}\"");
    }

    internal static string EscapeDigestValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// The single username to use for a realm — the only policy production and
    /// <see cref="TryBuildAuthorization"/> share. OwnTone's AirPlay 2 path answers
    /// with an empty username; classic RAOP speakers expect <c>iTunes</c>. A
    /// receiver that instead wants the literal <c>AirPlay</c> username has not
    /// been observed, so it is not speculatively probed.
    /// </summary>
    public static IReadOnlyList<string> UsernameCandidates(string realm) =>
        string.Equals(realm, "raop", StringComparison.OrdinalIgnoreCase)
            ? RaopUsername
            : EmptyUsername;

    public static bool TryParseChallenge(
        string? challenge,
        out string realm,
        out string nonce)
    {
        realm = string.Empty;
        nonce = string.Empty;
        if (string.IsNullOrWhiteSpace(challenge) ||
            !challenge.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var realmMatch = RealmRegex().Match(challenge);
        var nonceMatch = NonceRegex().Match(challenge);
        if (!realmMatch.Success || !nonceMatch.Success)
        {
            return false;
        }

        realm = realmMatch.Groups[1].Value;
        nonce = nonceMatch.Groups[1].Value;
        return realm.Length > 0 && nonce.Length > 0;
    }

    private static readonly string[] RaopUsername = ["iTunes"];
    private static readonly string[] EmptyUsername = [""];

    private static string Md5Hex(string value)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    [GeneratedRegex("realm=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RealmRegex();

    [GeneratedRegex("nonce=\"([^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonceRegex();
}
