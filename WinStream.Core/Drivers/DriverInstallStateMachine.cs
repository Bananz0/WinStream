namespace WinStream.Core.Drivers;

public enum DriverInstallState
{
    NotInstalled = 0,
    Checking,
    Downloading,
    Verifying,
    ReadyToInstall,
    Installing,
    RestartRequired,
    Detecting,
    Ready,
    Failed
}

public sealed class DriverInstallStateMachine
{
    private static readonly IReadOnlyDictionary<DriverInstallState, DriverInstallState[]> AllowedTransitions =
        new Dictionary<DriverInstallState, DriverInstallState[]>
        {
            [DriverInstallState.NotInstalled] = [DriverInstallState.Checking],
            [DriverInstallState.Checking] =
                [DriverInstallState.Downloading, DriverInstallState.Ready, DriverInstallState.Failed],
            [DriverInstallState.Downloading] =
                [DriverInstallState.Verifying, DriverInstallState.Failed],
            [DriverInstallState.Verifying] =
                [DriverInstallState.ReadyToInstall, DriverInstallState.Failed],
            [DriverInstallState.ReadyToInstall] =
                [DriverInstallState.Installing, DriverInstallState.Failed],
            [DriverInstallState.Installing] =
                [DriverInstallState.RestartRequired, DriverInstallState.Detecting, DriverInstallState.Failed],
            [DriverInstallState.RestartRequired] =
                [DriverInstallState.Detecting, DriverInstallState.Failed],
            [DriverInstallState.Detecting] =
                [DriverInstallState.Ready, DriverInstallState.NotInstalled, DriverInstallState.Failed],
            [DriverInstallState.Ready] =
                [DriverInstallState.NotInstalled, DriverInstallState.Failed],
            [DriverInstallState.Failed] = [DriverInstallState.Checking]
        };

    public DriverInstallState State { get; private set; } = DriverInstallState.NotInstalled;

    public int DownloadProgress { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool CanTransitionTo(DriverInstallState next) =>
        AllowedTransitions[State].Contains(next);

    public void TransitionTo(DriverInstallState next, string? errorMessage = null)
    {
        if (!CanTransitionTo(next))
        {
            throw new InvalidOperationException($"Cannot transition driver installation from {State} to {next}.");
        }

        if (next == DriverInstallState.Failed && string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new ArgumentException("A failure transition requires a user-facing error.", nameof(errorMessage));
        }

        State = next;
        ErrorMessage = next == DriverInstallState.Failed ? errorMessage : null;
        if (next != DriverInstallState.Downloading)
        {
            DownloadProgress = 0;
        }
    }

    public void ReportDownloadProgress(int percentage)
    {
        if (State != DriverInstallState.Downloading)
        {
            throw new InvalidOperationException("Download progress is only valid while downloading.");
        }

        DownloadProgress = Math.Clamp(percentage, 0, 100);
    }
}
