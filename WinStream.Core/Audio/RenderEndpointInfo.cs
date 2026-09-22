namespace WinStream.Core.Audio;

public sealed record RenderEndpointInfo(
    string Id,
    string FriendlyName,
    bool IsDefault,
    string? DeviceInstanceId = null)
{
    public override string ToString() =>
        IsDefault ? $"{FriendlyName} (Default)" : FriendlyName;
}
