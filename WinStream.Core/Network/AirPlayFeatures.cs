namespace WinStream.Core.Network;

/// <summary>Parses AirPlay TXT feature bitfields without depending on Streaming.</summary>
public static class AirPlayFeatures
{
    public static long Parse(string? featuresTxt)
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
}
