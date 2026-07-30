namespace WinStream.Core.Persistence;

public sealed class AppSettings
{
    public string? SelectedRenderDeviceId { get; set; }

    public bool MonitorCapture { get; set; }

    /// <summary>
    /// Experimental AirPlay 2 path. Off by default until pairing/PTP validation is green.
    /// </summary>
    public bool EnableAirPlay2Experimental { get; set; }
}
