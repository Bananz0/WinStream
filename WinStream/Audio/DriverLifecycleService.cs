using System;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Core.Drivers;

namespace WinStream.Audio;

public sealed class DriverLifecycleService : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private bool _operationInProgress;

    public DriverInstallStateMachine StateMachine { get; } = new();

#if DEBUG
    public bool CanAcquireDriver => true;
#else
    public bool CanAcquireDriver => false;
#endif

    public event EventHandler StateChanged;

    public async Task DownloadAndInstallAsync()
    {
        if (!CanAcquireDriver || _operationInProgress)
        {
            return;
        }

        _operationInProgress = true;
        try
        {
            TransitionTo(DriverInstallState.Checking);
            await PauseAsync(350);

            TransitionTo(DriverInstallState.Downloading);
            for (var progress = 5; progress <= 100; progress += 5)
            {
                await PauseAsync(70);
                StateMachine.ReportDownloadProgress(progress);
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            TransitionTo(DriverInstallState.Verifying);
            await PauseAsync(450);
            TransitionTo(DriverInstallState.ReadyToInstall);
            await PauseAsync(250);
            TransitionTo(DriverInstallState.Installing);
            await PauseAsync(650);
            TransitionTo(DriverInstallState.Detecting);
            await PauseAsync(450);
            TransitionTo(DriverInstallState.Ready);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (StateMachine.CanTransitionTo(DriverInstallState.Failed))
            {
                TransitionTo(
                    DriverInstallState.Failed,
                    "The virtual audio driver could not be prepared. Try again.");
            }
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    public Task RetryAsync() => DownloadAndInstallAsync();

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task PauseAsync(int milliseconds) =>
        await Task.Delay(milliseconds, _lifetime.Token);

    private void TransitionTo(DriverInstallState state, string errorMessage = null)
    {
        StateMachine.TransitionTo(state, errorMessage);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
