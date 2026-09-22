using System.Security.Cryptography;

namespace WinStream.Core.Persistence;

/// <summary>
/// The AirPlay 2 sender advertises a MAC-shaped device ID. Shared hard-coded IDs
/// collide when two WinStream installs share a LAN, so each install generates its own.
/// </summary>
public static class SenderIdentity
{
    public static string CreateLocallyAdministeredMac()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)((bytes[0] | 0x02) & 0xFE); // locally administered, unicast
        return string.Create(
            17,
            bytes,
            static (span, src) =>
            {
                const string hex = "0123456789ABCDEF";
                var o = 0;
                for (var i = 0; i < 6; i++)
                {
                    if (i > 0)
                    {
                        span[o++] = ':';
                    }

                    span[o++] = hex[src[i] >> 4];
                    span[o++] = hex[src[i] & 0xF];
                }
            });
    }

    public static bool LooksLikeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var hex = value.Replace(":", string.Empty).Replace("-", string.Empty);
        return hex.Length == 12 &&
               ulong.TryParse(
                   hex,
                   System.Globalization.NumberStyles.HexNumber,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out _);
    }
}
