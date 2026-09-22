namespace WinStream.Core.Network;

/// <summary>
/// Reads the receiver identity out of a <c>_raop</c> instance name such as
/// <c>AABBCCDDEEFF@living-room</c>.
/// </summary>
/// <remarks>
/// <c>deviceid</c> is advertised only on <c>_airplay</c>. A receiver that publishes
/// its two services on different addresses (macOS does) can lose that record on a
/// pass, and without a stable identity the same Mac lands in the device list twice.
/// The MAC prefix of the <c>_raop</c> instance name is the same identity.
/// </remarks>
public static class RaopInstanceName
{
    private const int MacHexLength = 12;

    /// <summary>
    /// Returns the colon-separated device ID encoded in the instance name, or an
    /// empty string when the name carries no MAC prefix.
    /// </summary>
    public static string DeviceIdOrEmpty(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return string.Empty;
        }

        var separator = instanceName.IndexOf('@');
        if (separator != MacHexLength)
        {
            return string.Empty;
        }

        var hex = instanceName.AsSpan(0, MacHexLength);
        foreach (var character in hex)
        {
            if (!Uri.IsHexDigit(character))
            {
                return string.Empty;
            }
        }

        return string.Create(MacHexLength + 5, instanceName, static (destination, source) =>
        {
            var written = 0;
            for (var i = 0; i < MacHexLength; i += 2)
            {
                if (written > 0)
                {
                    destination[written++] = ':';
                }

                destination[written++] = char.ToUpperInvariant(source[i]);
                destination[written++] = char.ToUpperInvariant(source[i + 1]);
            }
        });
    }

    /// <summary>
    /// Returns the part of <paramref name="instanceName"/> after its first
    /// <c>@</c> (the human-readable device name in a <c>_raop</c> instance name),
    /// or the whole string when there is no <c>@</c>.
    /// </summary>
    public static string NameAfterAtOrSelf(string? instanceName)
    {
        if (string.IsNullOrEmpty(instanceName))
        {
            return string.Empty;
        }

        var separator = instanceName.IndexOf('@');
        return separator >= 0 ? instanceName[(separator + 1)..] : instanceName;
    }
}
