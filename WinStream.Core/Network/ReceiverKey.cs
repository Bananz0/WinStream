namespace WinStream.Core.Network;

/// <summary>
/// One receiver identity for streaming sessions, device rows, discovery bookkeeping
/// and the remembered auto-connect target. <c>DeviceID</c> survives address changes;
/// address and port are the fallback for receivers that advertise no identity.
/// </summary>
public static class ReceiverKey
{
    public static string For(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.DeviceID)
            ? $"{device.IPAddress}:{device.Port}"
            : device.DeviceID;
    }

    public static bool Matches(string? key, DeviceInfo device) =>
        !string.IsNullOrWhiteSpace(key) &&
        string.Equals(key, For(device), StringComparison.Ordinal);

    public static bool SameReceiver(DeviceInfo left, DeviceInfo right) =>
        string.Equals(For(left), For(right), StringComparison.Ordinal);
}
