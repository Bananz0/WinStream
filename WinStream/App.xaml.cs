#nullable enable

using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Xaml;
using System;
using WinStream.Core;
using WinStream.Tray;

namespace WinStream
{
    public partial class App : Application
    {
        private MainWindow? _mainWindow;
        private TrayIconService? _trayIcon;
        private AppInstance? _appInstance;

        private static string LogDirectory => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinStream",
            "logs");

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            _appInstance = AppInstance.FindOrRegisterForKey(ProductIdentity.SingleInstanceKey);
            if (!_appInstance.IsCurrent)
            {
                var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
                await _appInstance.RedirectActivationToAsync(activation);
                Environment.Exit(0);
                return;
            }

            _appInstance.Activated += OnAppInstanceActivated;
            WinStream.Core.Logging.AppLog.EnableFileSink(LogDirectory);
            var startInTray = StartupRegistration.WasStartedFromLogin();

            _mainWindow = new MainWindow();
            if (startInTray)
            {
                _mainWindow.HideToTray();
            }
            else
            {
                _mainWindow.Activate();
            }

            _trayIcon = new TrayIconService(_mainWindow.WindowHandle);
            _trayIcon.MenuStateProvider = () => _mainWindow?.BuildTrayMenuState() ?? TrayMenuState.Empty;
            _trayIcon.OpenRequested += (_, _) => ShowMainWindow();
            _trayIcon.ConnectLastRequested += (_, _) => EnqueueTrayAction(
                window => window.ConnectLastFromTrayAsync());
            _trayIcon.ConnectDeviceRequested += (_, key) => EnqueueTrayAction(
                window => window.ConnectDeviceFromTrayAsync(key));
            _trayIcon.DisconnectRequested += (_, _) => EnqueueTrayAction(
                window => window.DisconnectFromTrayAsync());
            _trayIcon.ExitRequested += (_, _) => _ = QuitAsync();
            _trayIcon.Initialize();
        }

        private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            _mainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
        }

        private void ShowMainWindow()
        {
            _mainWindow?.ShowFromTray();
        }

        private void EnqueueTrayAction(Func<MainWindow, System.Threading.Tasks.Task> action)
        {
            var window = _mainWindow;
            if (window is null)
            {
                return;
            }

            window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await action(window);
                }
                catch (Exception ex)
                {
                    WinStream.Core.Logging.AppLog.Error(
                        "ui",
                        $"Tray action failed: {ex.GetType().Name}");
                }
            });
        }

        private async System.Threading.Tasks.Task QuitAsync()
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            if (_mainWindow is not null)
            {
                await _mainWindow.CloseForExitAsync();
                _mainWindow = null;
            }

            Exit();
        }
    }
}
