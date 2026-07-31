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
    /// Connect to the most recently used AirPlay receiver when it appears after launch.
    /// </summary>
    public bool AutoConnectLastReceiver { get; set; }

    /// <summary>
    /// Stable receiver identity (device ID when available, otherwise address and port).
    /// </summary>
    public string? LastReceiverKey { get; set; }

    /// <summary>
    /// Friendly name retained so the startup setting remains understandable while offline.
    /// </summary>
    public string? LastReceiverName { get; set; }

    /// <summary>
    /// Set once the user dismisses the hint about enabling AirPlay Receiver on a Mac.
    /// </summary>
    public bool AirPlayReceiverHintDismissed { get; set; }

    /// <summary>
    /// Store builds use loopback. VirtualDriver requires the optional sideload driver
    /// and is not user-selectable until a virtual-driver capture source exists.
    /// </summary>
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Loopback;

    /// <summary>
    /// The user's explicit choice to use the optional virtual endpoint when it is available.
    /// This preference is retained if the endpoint temporarily disappears.
    /// </summary>
    public bool PreferVirtualDriver { get; set; }

    /// <summary>
    /// Locally administered MAC used as the AirPlay 2 sender deviceID / PTP peer ID.
    /// Generated once per install when missing.
    /// </summary>
    public string? SenderDeviceId { get; set; }
}
