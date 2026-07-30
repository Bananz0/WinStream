namespace WinStream.Core.Streaming;

public enum AirPlayProtocolKind
{
    Unknown = 0,
    ClassicRaop = 1,
    AirPlay2 = 2
}

/// <summary>
/// Capability helpers for classic RAOP vs AirPlay 2. AP2 streaming remains gated.
/// </summary>
public static class AirPlayCapability
{
    // Community-observed feature bit often set on AP2-capable receivers (bit 30 of low word).
    public const long AirPlay2FeatureBit = 1L << 30;

    public static bool SupportsClassicRaop(bool hasReceiverPublicKey) =>
        hasReceiverPublicKey;

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
        if (airPlay2 && airPlay2GateEnabled)
        {
            return AirPlayProtocolKind.AirPlay2;
        }

        if (classic)
        {
            return AirPlayProtocolKind.ClassicRaop;
        }

        if (airPlay2)
        {
            return AirPlayProtocolKind.AirPlay2;
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
