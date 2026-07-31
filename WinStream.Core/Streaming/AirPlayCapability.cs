namespace WinStream.Core.Streaming;

public enum AirPlayProtocolKind
{
    Unknown = 0,
    ClassicRaop = 1,
    AirPlay2 = 2
}

/// <summary>RAOP TXT <c>et</c> encryption type selected for a session.</summary>
public enum RaopEncryptionMode
{
    /// <summary>Receiver advertises neither et=0 nor et=1.</summary>
    Unsupported = -1,

    /// <summary>et=0 — clear ALAC payloads for classic-compatible receivers.</summary>
    None = 0,

    /// <summary>et=1 — AES-128-CBC with the AirTunes RSA-wrapped key.</summary>
    Rsa = 1
}

/// <summary>
/// Capability helpers for classic RAOP vs AirPlay 2. AP2 streaming remains gated.
/// </summary>
public static class AirPlayCapability
{
    // Community-observed feature bit often set on AP2-capable receivers (bit 30 of low word).
    public const long AirPlay2FeatureBit = 1L << 30;

    /// <summary>
    /// Classic RAOP RSA encryption is advertised via TXT <c>et</c> containing
    /// type <c>1</c>. Device <c>pk</c> on modern receivers is Ed25519 identity,
    /// not proof of classic RAOP support.
    /// </summary>
    public static bool SupportsClassicRaop(string? encryptionTypesTxt) =>
        ResolveEncryptionMode(encryptionTypesTxt) != RaopEncryptionMode.Unsupported;

    /// <summary>
    /// Picks the strongest classic RAOP encryption we implement. RSA (et=1) is
    /// preferred; et=0 is clear ALAC for classic-compatible speakers.
    /// Modern macOS AirPlay Receiver often still rejects classic RTSP (OPTIONS 403)
    /// and needs AirPlay 2 HKP — do not treat et=0 alone as "Macs work on classic."
    /// et=3/5-only receivers are unsupported in the classic path.
    /// </summary>
    public static RaopEncryptionMode ResolveEncryptionMode(string? encryptionTypesTxt)
    {
        if (string.IsNullOrWhiteSpace(encryptionTypesTxt))
        {
            // Do not assume classic support — modern AP2 receivers still publish
            // pk (Ed25519), which previously caused false positives.
            return RaopEncryptionMode.Unsupported;
        }

        var types = encryptionTypesTxt
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        if (types.Contains("1"))
        {
            return RaopEncryptionMode.Rsa;
        }

        return types.Contains("0")
            ? RaopEncryptionMode.None
            : RaopEncryptionMode.Unsupported;
    }

    public static bool SupportsAirPlay2(
        bool hasPairingIdentity,
        long features,
        string? airPlayVersion)
    {
        if (hasPairingIdentity)
        {
            return true;
        }

        if ((features & AirPlay2FeatureBit) != 0)
        {
            return true;
        }

        // srcvers 366+ era commonly implies AP2 stack on Apple receivers.
        if (Version.TryParse(NormalizeVersion(airPlayVersion), out var version) &&
            version.Major >= 366)
        {
            return true;
        }

        return false;
    }

    public static AirPlayProtocolKind PreferredProtocol(
        bool classic,
        bool airPlay2,
        bool airPlay2GateEnabled)
    {
        // Phase 4+: prefer AP2 when the experimental gate is on for dual-capable
        // receivers; gate off keeps classic for dual-capable Macs that still need RAOP.
        return PreferredProtocolPhase4Target(classic, airPlay2, airPlay2GateEnabled);
    }

    /// <summary>
    /// Canonical prefer-AP2 routing (also used by <see cref="PreferredProtocol"/>).
    /// </summary>
    public static AirPlayProtocolKind PreferredProtocolPhase4Target(
        bool classic,
        bool airPlay2,
        bool airPlay2GateEnabled)
    {
        if (classic && airPlay2)
        {
            return airPlay2GateEnabled
                ? AirPlayProtocolKind.AirPlay2
                : AirPlayProtocolKind.ClassicRaop;
        }

        if (classic)
        {
            return AirPlayProtocolKind.ClassicRaop;
        }

        if (airPlay2)
        {
            return airPlay2GateEnabled
                ? AirPlayProtocolKind.AirPlay2
                : AirPlayProtocolKind.Unknown;
        }

        return AirPlayProtocolKind.Unknown;
    }

    public static void EnsureHomogeneousSelection(
        IEnumerable<AirPlayProtocolKind> selectedProtocols)
    {
        var set = selectedProtocols
            .Where(kind => kind is AirPlayProtocolKind.ClassicRaop or AirPlayProtocolKind.AirPlay2)
            .ToHashSet();
        if (set.Count > 1)
        {
            throw new InvalidOperationException(
                "Mixed AirPlay 1 and AirPlay 2 receivers are not supported. " +
                "Select only classic RAOP devices, or only AirPlay 2 devices.");
        }
    }

    public static long ParseFeatures(string? featuresTxt)
    {
        if (string.IsNullOrWhiteSpace(featuresTxt))
        {
            return 0;
        }

        // formats: "0x405F8A00,0x1C340" or "1080533440"
        var first = featuresTxt.Split(',', 2)[0].Trim();
        if (first.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(first, 16);
        }

        return long.TryParse(first, out var value) ? value : 0;
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "0";
        }

        var parts = version.Split('.', 4);
        return parts.Length switch
        {
            1 => $"{parts[0]}.0",
            _ => string.Join('.', parts.Take(3))
        };
    }
}
