#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

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
            return MapPackaged(task.State);
        }

        return new StartupRegistrationSnapshot(
            IsEnabled: IsUnpackagedRunEntryPresent(),
            CanToggle: true,
            StatusMessage: "Launch WinStream automatically when you sign in to Windows.");
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
            return MapPackaged(task.State);
        }

        SetUnpackagedRunEntry(enabled);
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
        return key?.GetValue(RunValueName) is string;
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

        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not resolve WinStream.exe path for startup.");
        key.SetValue(RunValueName, $"\"{exe}\" {LoginArgument}");
    }
}

internal readonly record struct StartupRegistrationSnapshot(
    bool IsEnabled,
    bool CanToggle,
    string StatusMessage);
