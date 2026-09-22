namespace WinStream.Core.Protocol.AirPlay2;

/// <summary>
/// Thrown when the receiver kept answering RTSP 401 after we retried with a
/// Digest response built from the supplied AirPlay password — the password
/// itself is wrong, not merely missing.
/// </summary>
public sealed class AirPlayPasswordRejectedException(string message) : InvalidOperationException(message)
{
}
