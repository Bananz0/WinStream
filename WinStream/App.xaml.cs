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
            _mainWindow = new MainWindow();
            _mainWindow.Activate();

            _trayIcon = new TrayIconService(_mainWindow.WindowHandle);
            _trayIcon.OpenRequested += (_, _) => ShowMainWindow();
            _trayIcon.ExitRequested += (_, _) => _ = QuitAsync();
            _trayIcon.Initialize();

            _mainWindow.HideToTray();
        }

        private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
        {
            _mainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
        }

        private void ShowMainWindow()
        {
            _mainWindow?.ShowFromTray();
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
