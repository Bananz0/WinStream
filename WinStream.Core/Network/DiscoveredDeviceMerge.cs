namespace WinStream.Core.Network;

/// <summary>
/// Folds a freshly scanned receiver onto what the previous pass already knew.
/// </summary>
/// <remarks>
/// A pass that resolves <c>_raop</c> but misses <c>_airplay</c> returns a device with
/// no flags, features, or public key. Taking it verbatim would drop the password
/// requirement from the device row, so known facts survive a partial pass while the
/// address and name always follow the newest record.
/// </remarks>
public static class DiscoveredDeviceMerge
{
    public static DeviceInfo CarryForward(DeviceInfo? previous, DeviceInfo current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return current;
        }

        current.Manufacturer = Prefer(current.Manufacturer, previous.Manufacturer);
        current.Model = Prefer(current.Model, previous.Model);
        current.DeviceID = Prefer(current.DeviceID, previous.DeviceID);
        current.ProtocolVersion = Prefer(current.ProtocolVersion, previous.ProtocolVersion);
        current.AirPlayVersion = Prefer(current.AirPlayVersion, previous.AirPlayVersion);
        current.PublicCUAirPlayPairingIdentity = Prefer(
            current.PublicCUAirPlayPairingIdentity,
            previous.PublicCUAirPlayPairingIdentity);
        current.PublicKey = Prefer(current.PublicKey, previous.PublicKey);
        current.EncryptionTypes = Prefer(current.EncryptionTypes, previous.EncryptionTypes);

        if (string.IsNullOrWhiteSpace(current.FeaturesRaw))
        {
            current.FeaturesRaw = previous.FeaturesRaw;
            current.Features = previous.Features;
        }

        if (string.IsNullOrWhiteSpace(current.StatusFlagsRaw))
        {
            current.StatusFlagsRaw = previous.StatusFlagsRaw;
            current.StatusFlags = previous.StatusFlags;
        }

        return current;
    }

    /// <summary>
    /// True when a tracked entry is the same receiver as <paramref name="device"/>
    /// under a different key — an address-keyed row left over from a pass that had
    /// no <c>deviceid</c>.
    /// </summary>
    public static bool IsStaleDuplicate(DeviceInfo tracked, DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(device);

        // Only an entry without its own identity can be a duplicate: two receivers
        // that both advertise a device ID are genuinely different receivers.
        if (!string.IsNullOrWhiteSpace(tracked.DeviceID) ||
            string.IsNullOrWhiteSpace(device.DeviceID))
        {
            return false;
        }

        return SameText(tracked.DisplayName, device.DisplayName) ||
               (SameText(tracked.IPAddress, device.IPAddress) && tracked.Port == device.Port);
    }

    private static string Prefer(string? current, string? previous) =>
        string.IsNullOrWhiteSpace(current) ? previous ?? string.Empty : current;

    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
