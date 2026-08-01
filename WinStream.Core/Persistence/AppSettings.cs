using WinStream.Core;

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
    /// Preferred launch-at-sign-in setting. Packaged installs also mirror Windows StartupTask state.
    /// </summary>
    public bool LaunchAtStartup { get; set; }

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

    /// <summary>Playback buffer preference. Default Auto starts ~250 ms and may climb.</summary>
    public PlaybackResponsiveness PlaybackResponsiveness { get; set; } = PlaybackResponsiveness.Auto;

    /// <summary>PCM conversion preference. Default Auto skips SRC when already 44.1 stereo.</summary>
    public AudioFidelity AudioFidelity { get; set; } = AudioFidelity.Auto;

    /// <summary>
    /// When false (default), Link UI is hidden and AirPlay behavior is unchanged.
    /// </summary>
    public bool LinkFeatureEnabled { get; set; }

    /// <summary>
    /// Extreme-only lab: try event-driven WASAPI loopback instead of the frozen 50 ms
    /// poll. Default false — enable in settings JSON to measure callback spacing.
    /// Does not unfreeze 50 ms for non-Extreme presets.
    /// </summary>
    public bool ExtremeEventDrivenCapture { get; set; }

    /// <summary>Active sink: AirPlay speakers or WinStream Link companion.</summary>
    public SinkMode SinkMode { get; set; } = SinkMode.AirPlay;

    /// <summary>Last Link companion key (IP:port).</summary>
    public string? LastLinkReceiverKey { get; set; }

    /// <summary>Friendly name for last Link companion.</summary>
    public string? LastLinkReceiverName { get; set; }

    /// <summary>Shallow copy for read-only snapshots from <see cref="AppSettingsService"/>.</summary>
    public AppSettings Clone() => new()
    {
        SelectedRenderDeviceId = SelectedRenderDeviceId,
        MonitorCapture = MonitorCapture,
        AutoConnectLastReceiver = AutoConnectLastReceiver,
        LastReceiverKey = LastReceiverKey,
        LastReceiverName = LastReceiverName,
        AirPlayReceiverHintDismissed = AirPlayReceiverHintDismissed,
        LaunchAtStartup = LaunchAtStartup,
        CaptureMode = CaptureMode,
        PreferVirtualDriver = PreferVirtualDriver,
        SenderDeviceId = SenderDeviceId,
        PlaybackResponsiveness = PlaybackResponsiveness,
        AudioFidelity = AudioFidelity,
        LinkFeatureEnabled = LinkFeatureEnabled,
        ExtremeEventDrivenCapture = ExtremeEventDrivenCapture,
        SinkMode = SinkMode,
        LastLinkReceiverKey = LastLinkReceiverKey,
        LastLinkReceiverName = LastLinkReceiverName
    };
}
