namespace WinStream.Core.Persistence;

public enum CaptureMode
{
    Loopback = 0,
    VirtualDriver = 1
}

public sealed class AppSettings
{
    public string? SelectedRenderDeviceId { get; set; }

    public bool MonitorCapture { get; set; }

    /// <summary>
    /// Experimental AirPlay 2 path. Off by default until pairing/PTP validation is green.
    /// </summary>
    public bool EnableAirPlay2Experimental { get; set; }

    /// <summary>
    /// Set once the user dismisses the hint about enabling AirPlay Receiver on a Mac.
    /// </summary>
    public bool AirPlayReceiverHintDismissed { get; set; }

    /// <summary>
    /// Store builds use loopback. VirtualDriver requires the optional sideload driver.
    /// </summary>
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Loopback;
}
