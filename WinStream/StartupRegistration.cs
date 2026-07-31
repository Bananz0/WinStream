#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;
using WinStream.Core.Logging;

namespace WinStream;

/// <summary>
/// Registers WinStream for launch at Windows sign-in.
/// Packaged installs use the declared <c>windows.startupTask</c>; unpackaged/debug uses HKCU Run.
/// </summary>
internal static class StartupRegistration
{
    public const string TaskId = "WinStreamStartup";
    public const string LoginArgument = "--started-from-login";
    private const string RunValueName = "WinStream";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsPackaged
    {
        get
        {
            try
            {
                _ = Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool WasStartedFromLogin()
    {
        try
        {
            var kind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent()
                .GetActivatedEventArgs()
                .Kind;
            if (kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask)
            {
                return true;
            }
        }
        catch
        {
            // Unpackaged / early init — fall through to argv.
        }

        return Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, LoginArgument, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<StartupRegistrationSnapshot> GetSnapshotAsync()
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            AppLog.Info("startup", $"Packaged startup task state={task.State}");
            return MapPackaged(task.State);
        }

        var present = IsUnpackagedRunEntryPresent();
        AppLog.Info("startup", $"Unpackaged Run entry present={present}");
        return new StartupRegistrationSnapshot(
            IsEnabled: present,
            CanToggle: true,
            StatusMessage: "Launch WinStream automatically when you sign in to Windows.");
    }

    /// <summary>
    /// Aligns Windows with the preference the user last chose in the app.
    /// The registration lives in Windows, not in settings.json, so it disappears whenever the
    /// app runs under a different identity (packaged vs unpackaged) or is reinstalled. Without
    /// this, the toggle silently reverts to Off even though the user never turned it off.
    /// </summary>
    public static async Task<StartupRegistrationSnapshot> ReconcileAsync(bool desired)
    {
        var snapshot = await GetSnapshotAsync();

        if (!desired || !snapshot.CanToggle)
        {
            return snapshot;
        }

        if (!snapshot.IsEnabled)
        {
            AppLog.Info("startup", "Saved preference is on but Windows is not registered; re-applying.");
            return await SetEnabledAsync(true);
        }

        if (!IsPackaged)
        {
            // A dev/sideload rebuild can move WinStream.exe, leaving a Run entry that
            // points at a path Windows can no longer launch.
            RefreshUnpackagedRunEntryPath();
        }

        return snapshot;
    }

    public static async Task<StartupRegistrationSnapshot> SetEnabledAsync(bool enabled)
    {
        if (IsPackaged)
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enabled)
            {
                if (task.State is StartupTaskState.Disabled or StartupTaskState.DisabledByUser)
                {
                    // DisabledByUser: RequestEnableAsync still surfaces Settings UI guidance via state.
                    await task.RequestEnableAsync();
                }
            }
            else if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            {
                task.Disable();
            }

            task = await StartupTask.GetAsync(TaskId);
            AppLog.Info("startup", $"Packaged startup set enabled={enabled}; state={task.State}");
            return MapPackaged(task.State);
        }

        SetUnpackagedRunEntry(enabled);
        AppLog.Info("startup", $"Unpackaged startup set enabled={enabled}");
        return await GetSnapshotAsync();
    }

    private static StartupRegistrationSnapshot MapPackaged(StartupTaskState state) =>
        state switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy =>
                new StartupRegistrationSnapshot(
                    IsEnabled: true,
                    CanToggle: state != StartupTaskState.EnabledByPolicy,
                    StatusMessage: "Launch WinStream automatically when you sign in to Windows."),
            StartupTaskState.DisabledByUser =>
                new StartupRegistrationSnapshot(
                    IsEnabled: false,
                    CanToggle: false,
                    StatusMessage: "Startup was turned off in Windows Settings > Apps > Startup. Enable WinStream there, then try again."),
            StartupTaskState.DisabledByPolicy =>
                new StartupRegistrationSnapshot(
                    IsEnabled: false,
                    CanToggle: false,
                    StatusMessage: "Startup is blocked by organization policy."),
            _ =>
                new StartupRegistrationSnapshot(
                    IsEnabled: false,
                    CanToggle: true,
                    StatusMessage: "Launch WinStream automatically when you sign in to Windows.")
        };

    private static bool IsUnpackagedRunEntryPresent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private static void RefreshUnpackagedRunEntryPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        if (key?.GetValue(RunValueName) is not string current)
        {
            return;
        }

        var expected = BuildUnpackagedRunCommand();
        if (string.Equals(current, expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppLog.Info("startup", "Run entry pointed at a stale executable path; rewriting.");
        SetUnpackagedRunEntry(true);
    }

    private static string BuildUnpackagedRunCommand()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve WinStream.exe path for startup.");
        return $"\"{exe}\" {LoginArgument}";
    }

    private static void SetUnpackagedRunEntry(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows Run registry key.");

        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(RunValueName, BuildUnpackagedRunCommand());
    }
}

internal readonly record struct StartupRegistrationSnapshot(
    bool IsEnabled,
    bool CanToggle,
    string StatusMessage);
