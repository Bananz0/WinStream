using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinStream.Core.Logging;

namespace WinStream.Core.Persistence;

/// <summary>
/// DPAPI-at-rest wrapper for the small JSON credential maps. Plaintext files written by
/// earlier builds still load, and are rewritten protected on the next save.
/// </summary>
internal static class ProtectedJsonEnvelope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Returns the storable payload, or <c>null</c> when Windows DPAPI is present but
    /// refused. Callers must abandon the write in that case: these maps hold long-term
    /// private keys, so a plaintext fallback would quietly downgrade secrets at rest.
    /// Off Windows there is no DPAPI, so the plain JSON is stored as-is.
    /// </summary>
    public static string? Wrap(string json, int version, string logCategory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return json;
        }

        try
        {
            var blob = Protect(Encoding.UTF8.GetBytes(json));
            return JsonSerializer.Serialize(
                new Envelope
                {
                    Version = version,
                    Protected = Convert.ToBase64String(blob)
                },
                JsonOptions);
        }
        catch (CryptographicException ex)
        {
            AppLog.Error(logCategory, $"DPAPI protect failed; not writing credentials: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>False when <paramref name="raw"/> is not a protected envelope of this version.</summary>
    public static bool TryUnwrap(string raw, int version, out string json)
    {
        json = string.Empty;
        Envelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (envelope?.Protected is null || envelope.Version != version)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected credentials require Windows DPAPI.");
        }

        json = Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(envelope.Protected)));
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] blob) =>
        ProtectedData.Unprotect(blob, optionalEntropy: null, DataProtectionScope.CurrentUser);

    private sealed class Envelope
    {
        public int Version { get; set; }

        public string? Protected { get; set; }
    }
}
