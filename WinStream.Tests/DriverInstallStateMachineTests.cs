using WinStream.Core.Drivers;

namespace WinStream.Tests;

public class DriverInstallStateMachineTests
{
    public static TheoryData<DriverInstallState, DriverInstallState> LegalTransitions =>
        new()
        {
            { DriverInstallState.NotInstalled, DriverInstallState.Checking },
            { DriverInstallState.Checking, DriverInstallState.Downloading },
            { DriverInstallState.Checking, DriverInstallState.Ready },
            { DriverInstallState.Checking, DriverInstallState.Failed },
            { DriverInstallState.Downloading, DriverInstallState.Verifying },
            { DriverInstallState.Downloading, DriverInstallState.Failed },
            { DriverInstallState.Verifying, DriverInstallState.ReadyToInstall },
            { DriverInstallState.Verifying, DriverInstallState.Failed },
            { DriverInstallState.ReadyToInstall, DriverInstallState.Installing },
            { DriverInstallState.ReadyToInstall, DriverInstallState.Failed },
            { DriverInstallState.Installing, DriverInstallState.RestartRequired },
            { DriverInstallState.Installing, DriverInstallState.Detecting },
            { DriverInstallState.Installing, DriverInstallState.Failed },
            { DriverInstallState.RestartRequired, DriverInstallState.Detecting },
            { DriverInstallState.RestartRequired, DriverInstallState.Failed },
            { DriverInstallState.Detecting, DriverInstallState.Ready },
            { DriverInstallState.Detecting, DriverInstallState.NotInstalled },
            { DriverInstallState.Detecting, DriverInstallState.Failed },
            { DriverInstallState.Ready, DriverInstallState.NotInstalled },
            { DriverInstallState.Ready, DriverInstallState.Failed },
            { DriverInstallState.Failed, DriverInstallState.Checking }
        };

    public static TheoryData<DriverInstallState[]> LegalPaths =>
        new()
        {
            new[]
            {
                DriverInstallState.Checking,
                DriverInstallState.Downloading,
                DriverInstallState.Verifying,
                DriverInstallState.ReadyToInstall,
                DriverInstallState.Installing,
                DriverInstallState.Detecting,
                DriverInstallState.Ready
            },
            new[]
            {
                DriverInstallState.Checking,
                DriverInstallState.Downloading,
                DriverInstallState.Verifying,
                DriverInstallState.ReadyToInstall,
                DriverInstallState.Installing,
                DriverInstallState.RestartRequired,
                DriverInstallState.Detecting,
                DriverInstallState.Ready
            },
            new[]
            {
                DriverInstallState.Checking,
                DriverInstallState.Failed,
                DriverInstallState.Checking,
                DriverInstallState.Ready
            }
        };

    [Theory]
    [MemberData(nameof(LegalPaths))]
    [Trait("Area", "Driver")]
    public void TransitionTo_accepts_legal_paths(DriverInstallState[] path)
    {
        var machine = new DriverInstallStateMachine();

        foreach (var state in path)
        {
            machine.TransitionTo(
                state,
                state == DriverInstallState.Failed ? "Download failed." : null);
        }

        Assert.Equal(path[^1], machine.State);
    }

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    [Trait("Area", "Driver")]
    public void TransitionTo_accepts_every_legal_transition(
        DriverInstallState start,
        DriverInstallState next)
    {
        var machine = MoveTo(start);

        machine.TransitionTo(
            next,
            next == DriverInstallState.Failed ? "Failed." : null);

        Assert.Equal(next, machine.State);
    }

    [Fact]
    [Trait("Area", "Driver")]
    public void TransitionTo_rejects_every_illegal_transition()
    {
        foreach (var start in Enum.GetValues<DriverInstallState>())
        {
            foreach (var next in Enum.GetValues<DriverInstallState>())
            {
                var machine = MoveTo(start);
                if (machine.CanTransitionTo(next))
                {
                    continue;
                }

                Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(next));
            }
        }
    }

    [Fact]
    [Trait("Area", "Driver")]
    public void Failure_requires_a_user_facing_message()
    {
        var machine = new DriverInstallStateMachine();
        machine.TransitionTo(DriverInstallState.Checking);

        Assert.Throws<ArgumentException>(() => machine.TransitionTo(DriverInstallState.Failed));
    }

    [Fact]
    [Trait("Area", "Driver")]
    public void Download_progress_is_clamped_and_cleared_after_download()
    {
        var machine = new DriverInstallStateMachine();
        machine.TransitionTo(DriverInstallState.Checking);
        machine.TransitionTo(DriverInstallState.Downloading);

        machine.ReportDownloadProgress(120);
        Assert.Equal(100, machine.DownloadProgress);

        machine.TransitionTo(DriverInstallState.Verifying);
        Assert.Equal(0, machine.DownloadProgress);
    }

    [Fact]
    [Trait("Area", "Driver")]
    public void Download_progress_is_rejected_outside_download()
    {
        var machine = new DriverInstallStateMachine();

        Assert.Throws<InvalidOperationException>(() => machine.ReportDownloadProgress(10));
    }

    private static DriverInstallStateMachine MoveTo(DriverInstallState target)
    {
        var machine = new DriverInstallStateMachine();
        if (target == DriverInstallState.NotInstalled)
        {
            return machine;
        }

        DriverInstallState[] path = target switch
        {
            DriverInstallState.Checking => [DriverInstallState.Checking],
            DriverInstallState.Downloading =>
                [DriverInstallState.Checking, DriverInstallState.Downloading],
            DriverInstallState.Verifying =>
                [DriverInstallState.Checking, DriverInstallState.Downloading, DriverInstallState.Verifying],
            DriverInstallState.ReadyToInstall =>
                [
                    DriverInstallState.Checking,
                    DriverInstallState.Downloading,
                    DriverInstallState.Verifying,
                    DriverInstallState.ReadyToInstall
                ],
            DriverInstallState.Installing =>
                [
                    DriverInstallState.Checking,
                    DriverInstallState.Downloading,
                    DriverInstallState.Verifying,
                    DriverInstallState.ReadyToInstall,
                    DriverInstallState.Installing
                ],
            DriverInstallState.RestartRequired =>
                [
                    DriverInstallState.Checking,
                    DriverInstallState.Downloading,
                    DriverInstallState.Verifying,
                    DriverInstallState.ReadyToInstall,
                    DriverInstallState.Installing,
                    DriverInstallState.RestartRequired
                ],
            DriverInstallState.Detecting =>
                [
                    DriverInstallState.Checking,
                    DriverInstallState.Downloading,
                    DriverInstallState.Verifying,
                    DriverInstallState.ReadyToInstall,
                    DriverInstallState.Installing,
                    DriverInstallState.Detecting
                ],
            DriverInstallState.Ready =>
                [DriverInstallState.Checking, DriverInstallState.Ready],
            DriverInstallState.Failed =>
                [DriverInstallState.Checking, DriverInstallState.Failed],
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        foreach (var state in path)
        {
            machine.TransitionTo(
                state,
                state == DriverInstallState.Failed ? "Failed." : null);
        }

        return machine;
    }
}
