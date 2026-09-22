namespace WinStream.Core.Network;

/// <summary>
/// Parses AirPlay TXT <c>sf</c>/<c>flags</c> without depending on Streaming.
/// Bit <c>0x80</c> is the documented PasswordRequired flag.
/// </summary>
public static class AirPlayStatusFlags
{
    /// <summary>Receiver has an AirPlay password set.</summary>
    public const long PasswordRequired = 0x80;

    public static long Parse(string? flagsTxt)
    {
        if (string.IsNullOrWhiteSpace(flagsTxt))
        {
            return 0;
        }

        var first = flagsTxt.Split(',', 2)[0].Trim();
        if (first.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(first, 16);
        }

        return long.TryParse(first, out var value) ? value : 0;
    }

    public static bool RequiresPassword(long flags) =>
        (flags & PasswordRequired) != 0;
}
