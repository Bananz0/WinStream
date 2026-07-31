namespace WinStream.Core.Streaming.Link;

/// <summary>
/// Pure XOR policy: AirPlay and Link never run concurrently.
/// </summary>
public static class SinkModeSwitchPolicy
{
    public static bool RequiresTeardown(SinkMode from, SinkMode to) =>
        from != to;

    public static bool AllowsAirPlayAutoConnect(SinkMode mode) =>
        mode == SinkMode.AirPlay;

    public static bool AllowsLinkAutoConnect(SinkMode mode) =>
        mode == SinkMode.Link;

    public static string ConfirmMessage(SinkMode from, SinkMode to) =>
        (from, to) switch
        {
            (SinkMode.AirPlay, SinkMode.Link) =>
                "Switching to WinStream Link will disconnect AirPlay speakers. Continue?",
            (SinkMode.Link, SinkMode.AirPlay) =>
                "Switching to AirPlay will stop the Link companion stream. Continue?",
            _ => "Switch sink mode?"
        };
}
