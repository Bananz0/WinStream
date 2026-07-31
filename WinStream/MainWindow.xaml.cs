using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Audio;
using WinStream.Core.Audio;
using WinStream.Core.Drivers;
using WinStream.Core.Logging;
using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Streaming;
using WinStream.Network;
using WinStream.Streaming;
using WinStream.ViewModels;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        /// <summary>Receiver volume floor in dB; the slider maps 0-100% onto this range.</summary>
        private const double MinVolumeDb = -30.0;

        private const double PreferredWidthDips = 900;
        private const double PreferredHeightDips = 780;

        public ObservableCollection<DeviceViewModel> DeviceList { get; } = new();

        private readonly List<DeviceViewModel> _allDevices = new();
        private readonly AppSettingsService _settings = new();
        private readonly CaptureMonitorService _captureMonitor;
        private readonly DriverLifecycleService _driverLifecycle = new();
        private readonly StreamingOrchestrator _streamingOrchestrator;
        private readonly DeviceDiscoveryCoordinator _discovery = new();
        private readonly AutoConnectAttemptTracker _autoConnectAttempts = new();
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _captureLevelTimer;
        private readonly AppWindow _appWindow;
        private string _filterText = string.Empty;
        private bool _allowClose;
        private bool _connectionInFlight;
        private bool _driverReadyPromptShown;
        private bool _suppressCaptureSelectionEvents;
        private bool _suppressAutoConnectEvents;

        public MainWindow()
        {
            InitializeComponent();
            _captureMonitor = new CaptureMonitorService(_settings);
            _streamingOrchestrator = new StreamingOrchestrator(_settings.EnsureSenderDeviceId());
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Closing += OnAppWindowClosing;
            ApplyWindowIcon();

            // The window only reports its real DPI once it is shown on a monitor.
            Activated += OnFirstActivated;

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _scanTimer.Tick += async (_, _) => await DiscoverAndDisplayDevicesAsync();
            _scanTimer.Start();

            _captureLevelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _captureLevelTimer.Tick += (_, _) => UpdateCaptureLevelUi();
            _captureLevelTimer.Start();

            _captureMonitor.StateChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(RefreshCaptureStatus);
            _driverLifecycle.StateChanged += OnDriverLifecycleStateChanged;
            _streamingOrchestrator.StateChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(OnStreamingStateChanged);

            LoadCaptureEndpoints();
            RestoreCaptureSettings();
            RestoreAutoConnectSetting();
            RefreshDriverUi();
            UpdateVolumeReadout(streamVolumeSlider.Value);
            RefreshSessionStatus();
            _ = DiscoverAndDisplayDevicesAsync();
        }

        public IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

        public void ShowFromTray()
        {
            _appWindow.Show();
            Activate();
        }

        public void HideToTray()
        {
            _appWindow.Hide();
        }

        public async Task CloseForExitAsync()
        {
            _allowClose = true;
            _scanTimer.Stop();
            _captureLevelTimer.Stop();

            // Tear down streaming first: the orchestrator is still subscribed to the
            // capture source and would pump frames into a disposed WASAPI client.
            await _streamingOrchestrator.DisposeAsync();
            await _captureMonitor.DisposeAsync();
            _driverLifecycle.Dispose();
            Close();
        }

        /// <summary>
        /// A WinUI window does not inherit the executable's embedded icon, so the taskbar
        /// button and Alt+Tab entry stay blank unless the icon is assigned explicitly.
        /// </summary>
        private void ApplyWindowIcon()
        {
            var iconPath = System.IO.Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "WinStreamTray.ico");
            if (System.IO.File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }
        }

        private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            Activated -= OnFirstActivated;
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                ApplyPreferredWindowSize);
        }

        private void ApplyPreferredWindowSize()
        {
            var dpi = GetDpiForWindow(WindowHandle);
            var scale = dpi > 0 ? dpi / 96.0 : 1.0;

            var work = DisplayArea
                .GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest)
                .WorkArea;
            var width = Math.Min((int)(PreferredWidthDips * scale), (int)(work.Width * 0.9));
            var height = Math.Min((int)(PreferredHeightDips * scale), (int)(work.Height * 0.9));

            _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                work.X + ((work.Width - width) / 2),
                work.Y + ((work.Height - height) / 2),
                width,
                height));
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_allowClose)
            {
                return;
            }

            args.Cancel = true;
            sender.Hide();
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await DiscoverAndDisplayDevicesAsync(showProgress: true);
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCaptureEndpoints();
        }

        private async void DriverActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_driverLifecycle.CanAcquireDriver)
            {
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri("https://github.com/bananz0/WinStream/releases"));
                return;
            }

            driverActionButton.IsEnabled = false;
            await _driverLifecycle.DownloadAndInstallAsync();
        }

        private void OnDriverLifecycleStateChanged(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                RefreshDriverUi();
                if (_driverLifecycle.StateMachine.State == DriverInstallState.Ready &&
                    !_driverReadyPromptShown)
                {
                    _driverReadyPromptShown = true;
                    await PromptToUseVirtualDriverAsync();
                }
            });
        }

        private void RefreshDriverUi()
        {
            var machine = _driverLifecycle.StateMachine;
            var isBusy = machine.State is
                DriverInstallState.Checking or
                DriverInstallState.Downloading or
                DriverInstallState.Verifying or
                DriverInstallState.ReadyToInstall or
                DriverInstallState.Installing or
                DriverInstallState.Detecting;

            driverProgressPanel.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
            driverProgressBar.IsIndeterminate = machine.State != DriverInstallState.Downloading;
            driverProgressBar.Value = machine.DownloadProgress;
            driverActionButton.IsEnabled = false;

            switch (machine.State)
            {
                case DriverInstallState.NotInstalled:
                    driverStateIcon.Glyph = "\uE72E";
                    driverStateBadge.Text = _driverLifecycle.CanAcquireDriver ? "Optional" : "Coming soon";
                    driverStatusText.Text = _driverLifecycle.CanAcquireDriver
                        ? "Add a dedicated WinStream output. The download is verified before Windows asks to install it."
                        : "Signed driver downloads are not available in this build yet.";
                    driverActionButton.Content = _driverLifecycle.CanAcquireDriver
                        ? "Download & install"
                        : "Learn more";
                    driverActionButton.IsEnabled = true;
                    AutomationProperties.SetName(
                        driverActionButton,
                        _driverLifecycle.CanAcquireDriver
                            ? "Download and install WinStream virtual audio driver"
                            : "Learn more about WinStream virtual audio driver");
                    break;
                case DriverInstallState.Checking:
                    SetDriverProgress("Checking", "Checking compatibility…");
                    break;
                case DriverInstallState.Downloading:
                    SetDriverProgress(
                        "Downloading",
                        $"Downloading securely… {machine.DownloadProgress}%");
                    break;
                case DriverInstallState.Verifying:
                    SetDriverProgress("Verifying", "Verifying the download and publisher…");
                    break;
                case DriverInstallState.ReadyToInstall:
                    SetDriverProgress("Ready", "Download verified. Preparing Windows installation…");
                    break;
                case DriverInstallState.Installing:
                    SetDriverProgress("Installing", "Waiting for Windows to finish installation…");
                    break;
                case DriverInstallState.RestartRequired:
                    driverStateBadge.Text = "Restart required";
                    driverStatusText.Text = "Restart Windows to finish installing the virtual audio driver.";
                    driverActionButton.Content = "Restart later";
                    break;
                case DriverInstallState.Detecting:
                    SetDriverProgress("Finishing", "Looking for the new WinStream audio endpoint…");
                    break;
                case DriverInstallState.Ready:
                    driverStateIcon.Glyph = "\uE73E";
                    driverStateBadge.Text = "Ready";
                    driverStatusText.Text = _settings.Settings.PreferVirtualDriver
                        ? "Installed and selected for your next connection."
                        : "Installed. System audio remains selected until you choose to switch.";
                    driverActionButton.Content = "Installed";
                    virtualDriverCard.Background = ThemeBrush("SystemFillColorSuccessBackgroundBrush");
                    virtualDriverCard.BorderBrush = ThemeBrush("SystemFillColorSuccessBrush");
                    break;
                case DriverInstallState.Failed:
                    driverStateIcon.Glyph = "\uEA39";
                    driverStateBadge.Text = "Needs attention";
                    driverStatusText.Text = machine.ErrorMessage ?? "The driver could not be prepared.";
                    driverActionButton.Content = "Retry";
                    driverActionButton.IsEnabled = _driverLifecycle.CanAcquireDriver;
                    AutomationProperties.SetName(driverActionButton, "Retry virtual audio driver installation");
                    break;
            }

            AutomationProperties.SetName(
                virtualDriverCard,
                $"WinStream virtual audio driver, {driverStateBadge.Text}");
            AutomationProperties.SetHelpText(virtualDriverCard, driverStatusText.Text);
        }

        private void SetDriverProgress(string badge, string status)
        {
            driverStateBadge.Text = badge;
            driverStatusText.Text = status;
            driverProgressText.Text = status;
            driverActionButton.Content = "Please wait";
        }

        private async Task PromptToUseVirtualDriverAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "WinStream driver is ready",
                Content = "Use the dedicated WinStream audio source for future connections? " +
                          "Your current stream will not be interrupted.",
                PrimaryButtonText = "Use virtual driver",
                CloseButtonText = "Keep system audio",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            var preferDriver = result == ContentDialogResult.Primary;
            _settings.Update(settings => settings.PreferVirtualDriver = preferDriver);
            RefreshDriverUi();

            if (preferDriver)
            {
                ShowMessage(
                    InfoBarSeverity.Success,
                    "Virtual audio selected",
                    "WinStream will use the dedicated source on your next connection.");
            }
        }

        private void FilterBox_TextChanged(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            _filterText = sender.Text?.Trim() ?? string.Empty;
            RebuildVisibleDevices();
        }

        private async void CaptureDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCaptureSelectionEvents)
            {
                return;
            }

            if (captureDeviceComboBox.SelectedItem is CaptureEndpointViewModel endpoint)
            {
                await _captureMonitor.SetSelectedEndpointAsync(endpoint.Id);
                RefreshCaptureStatus();
            }
        }

        private async void MonitorCaptureToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressCaptureSelectionEvents)
            {
                return;
            }

            await _captureMonitor.SetMonitoringAsync(monitorCaptureToggle.IsOn);
            RefreshCaptureStatus();
        }

        private void LoadCaptureEndpoints()
        {
            _suppressCaptureSelectionEvents = true;
            try
            {
                var endpoints = _captureMonitor.ListEndpoints()
                    .Select(endpoint => new CaptureEndpointViewModel(endpoint))
                    .ToList();
                captureDeviceComboBox.ItemsSource = endpoints;

                var selectedId = _captureMonitor.SelectedEndpointId;
                captureDeviceComboBox.SelectedItem =
                    endpoints.FirstOrDefault(e =>
                        string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                    ?? endpoints.FirstOrDefault(e => e.Endpoint.IsDefault)
                    ?? endpoints.FirstOrDefault();
            }
            catch (Exception ex)
            {
                AppLog.Warn("capture", $"Failed to enumerate capture endpoints: {ex.GetType().Name}");
                ShowMessage(
                    InfoBarSeverity.Error,
                    "No audio devices available",
                    "Windows didn't return any playback devices to capture from.");
            }
            finally
            {
                _suppressCaptureSelectionEvents = false;
            }
        }

        private void RestoreCaptureSettings()
        {
            _suppressCaptureSelectionEvents = true;
            try
            {
                monitorCaptureToggle.IsOn = _captureMonitor.IsMonitoring;
            }
            finally
            {
                _suppressCaptureSelectionEvents = false;
            }

            if (_captureMonitor.IsMonitoring)
            {
                _ = _captureMonitor.SetMonitoringAsync(true);
            }

            RefreshCaptureStatus();
        }

        private void RestoreAutoConnectSetting()
        {
            _suppressAutoConnectEvents = true;
            try
            {
                autoConnectToggle.IsOn = _settings.Settings.AutoConnectLastReceiver;
                RefreshAutoConnectDescription();
            }
            finally
            {
                _suppressAutoConnectEvents = false;
            }
        }

        private async void AutoConnectToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoConnectEvents)
            {
                return;
            }

            var enabled = autoConnectToggle.IsOn;
            _settings.Update(settings => settings.AutoConnectLastReceiver = enabled);
            _autoConnectAttempts.Reset();
            RefreshAutoConnectDescription();

            if (enabled)
            {
                await TryAutoConnectToLastReceiverAsync();
            }
        }

        private void RefreshAutoConnectDescription()
        {
            var receiverName = _settings.Settings.LastReceiverName;
            if (string.IsNullOrWhiteSpace(receiverName))
            {
                autoConnectDescriptionText.Text =
                    "Your next successful connection will become the startup device.";
                return;
            }

            autoConnectDescriptionText.Text = autoConnectToggle.IsOn
                ? $"Automatically reconnect to {receiverName} as soon as it appears."
                : $"Your last device was {receiverName}. Turn this on to reconnect to it automatically.";
        }

        private void UpdateCaptureLevelUi()
        {
            if (!_captureMonitor.IsCapturing)
            {
                captureLevelBar.Value = 0;
                return;
            }

            // Soften display: RMS is typically << 1.0 for normal content.
            captureLevelBar.Value = Math.Clamp(_captureMonitor.CurrentRms * 4.0, 0, 1);
        }

        private void RefreshCaptureStatus()
        {
            if (!_captureMonitor.IsCapturing)
            {
                captureStatusText.Text = "Idle";
                captureStatusText.Foreground = ThemeBrush("TextFillColorSecondaryBrush");
                return;
            }

            if (_captureMonitor.IsSilent)
            {
                captureStatusText.Text = "Silent";
                captureStatusText.Foreground = ThemeBrush("SystemFillColorCautionBrush");
                return;
            }

            var format = _captureMonitor.Format;
            captureStatusText.Text = format is null ? "Capturing" : format.ToString();
            captureStatusText.Foreground = ThemeBrush("SystemFillColorSuccessBrush");
        }

        private async void StreamVolumeSlider_ValueChanged(
            object sender,
            RangeBaseValueChangedEventArgs e)
        {
            UpdateVolumeReadout(e.NewValue);

            if (_streamingOrchestrator.State == SessionState.Streaming)
            {
                await _streamingOrchestrator.SetVolumeAsync(PercentToDb(e.NewValue));
            }
        }

        private void UpdateVolumeReadout(double percent)
        {
            if (streamVolumeText is not null)
            {
                streamVolumeText.Text = $"{percent:0}%";
            }
        }

        private static float PercentToDb(double percent) =>
            percent <= 0
                ? -144f
                : (float)(MinVolumeDb - (MinVolumeDb * percent / 100.0));

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: DeviceViewModel device })
            {
                return;
            }

            await SetDeviceConnectionAsync(device, connect: !device.IsConnected);
        }

        /// <summary>
        /// The single connect/disconnect path. One attempt runs at a time so an automatic
        /// connection and a button press cannot race into the exclusive AirPlay 2 session.
        /// </summary>
        private async Task SetDeviceConnectionAsync(
            DeviceViewModel device,
            bool connect,
            bool isAutomatic = false)
        {
            if (_connectionInFlight)
            {
                return;
            }

            _connectionInFlight = true;
            messageBar.IsOpen = false;
            device.ClearStatus();
            if (connect && isAutomatic)
            {
                device.SetStatus("Found — connecting automatically…", DeviceStatusKind.Neutral);
            }

            device.IsBusy = true;
            UpdateUI(false);

            try
            {
                if (!connect)
                {
                    await _streamingOrchestrator.DisconnectAsync(device.Device);
                    device.SetStatus("Disconnected.", DeviceStatusKind.Neutral);
                    AppLog.Info("ui", "Disconnected from receiver.");
                    await StopCaptureIfIdleAsync();
                    return;
                }

                AppLog.Info("ui", "Connecting to selected receiver.");
                await _captureMonitor.EnsureStartedAsync();
                var source = _captureMonitor.GetSourceForStreaming()
                    ?? throw new InvalidOperationException(
                        "Windows didn't provide an audio source to capture.");

                await _streamingOrchestrator.ConnectAsync(device.Device, source);
                await _streamingOrchestrator.SetVolumeAsync(PercentToDb(streamVolumeSlider.Value));
                RememberReceiver(device);
                _autoConnectAttempts.RecordSuccess();

                if (_streamingOrchestrator.State == SessionState.Degraded)
                {
                    device.SetStatus("Connected, but the stream is degraded.", DeviceStatusKind.Caution);
                }
                else
                {
                    device.SetStatus("Streaming.", DeviceStatusKind.Success);
                }
            }
            catch (Exception ex)
            {
                if (isAutomatic)
                {
                    _autoConnectAttempts.RecordFailure();
                }

                device.SetStatus(
                    isAutomatic ? "Automatic connection failed. Retrying later." : "Couldn't connect.",
                    DeviceStatusKind.Error);
                if (!isAutomatic)
                {
                    ShowMessage(
                        InfoBarSeverity.Error,
                        $"Couldn't connect to {device.DisplayName}",
                        FormatConnectionFailure(ex.Message));
                }

                AppLog.Error("ui", $"Connection error: {ex.GetType().Name}");
            }
            finally
            {
                _connectionInFlight = false;
                device.IsBusy = false;
                UpdateUI(true);
                SyncConnectionState();
                RefreshSessionStatus();
            }
        }

        private void RememberReceiver(DeviceViewModel device)
        {
            var key = device.Key;
            var name = device.DisplayName;
            var changed = !string.Equals(
                _settings.Settings.LastReceiverKey,
                key,
                StringComparison.Ordinal);

            _settings.Update(settings =>
            {
                settings.LastReceiverKey = key;
                settings.LastReceiverName = name;
            });

            if (changed)
            {
                _autoConnectAttempts.Reset();
            }

            RefreshAutoConnectDescription();
        }

        /// <summary>
        /// Loopback capture only exists for streaming unless the user asked to monitor,
        /// so it must not keep running once the last receiver is gone.
        /// </summary>
        private async Task StopCaptureIfIdleAsync()
        {
            if (_captureMonitor.IsMonitoring ||
                _streamingOrchestrator.ConnectedReceivers.Count > 0)
            {
                return;
            }

            await _captureMonitor.StopAsync();
        }

        /// <summary>
        /// Background rescans stay silent so the list doesn't flicker every five
        /// seconds; only an explicit scan reports progress.
        /// </summary>
        private async Task DiscoverAndDisplayDevicesAsync(bool showProgress = false)
        {
            var announce = showProgress || _allDevices.Count == 0;
            if (announce)
            {
                searchButton.IsEnabled = false;
                deviceCountText.Text = "Looking for devices…";
            }

            try
            {
                var present = await _discovery.ScanAsync(IsConnectedKey);
                if (present is null)
                {
                    return;
                }

                ApplyDiscoveredDevices(present);
                RebuildVisibleDevices();
                RefreshAirPlayReceiverHint();
                await TryAutoConnectToLastReceiverAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("ui", $"Discovery error: {ex.GetType().Name}");
                deviceCountText.Text = "Couldn't scan the network.";
            }
            finally
            {
                // A connect attempt owns the button while it runs, so a scan finishing
                // underneath it must not re-enable the toolbar.
                if (announce && !_connectionInFlight)
                {
                    searchButton.IsEnabled = true;
                }
            }
        }

        private bool IsConnectedKey(string key) =>
            _allDevices.Any(device =>
                device.IsConnected && string.Equals(device.Key, key, StringComparison.Ordinal));

        private async Task TryAutoConnectToLastReceiverAsync()
        {
            var settings = _settings.Settings;
            if (!AutoConnectPolicy.ShouldAttempt(
                    settings.AutoConnectLastReceiver,
                    settings.LastReceiverKey,
                    _streamingOrchestrator.State,
                    _connectionInFlight,
                    _autoConnectAttempts.AttemptsAvailable))
            {
                return;
            }

            var target = AutoConnectPolicy.FindTarget(
                _allDevices.Select(device => device.Device),
                settings.LastReceiverKey);
            if (target is null)
            {
                return;
            }

            var key = ReceiverKey.For(target);
            var row = _allDevices.FirstOrDefault(device =>
                string.Equals(device.Key, key, StringComparison.Ordinal));
            if (row is null)
            {
                return;
            }

            AppLog.Info("ui", "Remembered receiver found; connecting automatically.");
            await SetDeviceConnectionAsync(row, connect: true, isAutomatic: true);
        }

        /// <summary>
        /// Folds the receivers the coordinator reports as present into the existing rows,
        /// so UI state (busy, status, connection) survives the five-second rescan.
        /// </summary>
        private void ApplyDiscoveredDevices(IReadOnlyList<DeviceInfo> present)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var device in present)
            {
                var key = DeviceViewModel.BuildKey(device);
                seen.Add(key);

                var existing = _allDevices.FirstOrDefault(row =>
                    string.Equals(row.Key, key, StringComparison.Ordinal));
                if (existing is null)
                {
                    _allDevices.Add(new DeviceViewModel(device));
                }
                else
                {
                    existing.Update(device);
                }
            }

            _allDevices.RemoveAll(row => !seen.Contains(row.Key));
            _allDevices.Sort((left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        }

        /// <summary>
        /// Syncs the bound collection in place rather than rebuilding it, so a rescan
        /// every five seconds doesn't reset scroll position or flash the list.
        /// </summary>
        private void RebuildVisibleDevices()
        {
            var target = string.IsNullOrWhiteSpace(_filterText)
                ? _allDevices.ToList()
                : _allDevices.Where(device => device.MatchesFilter(_filterText)).ToList();

            for (var i = DeviceList.Count - 1; i >= 0; i--)
            {
                if (!target.Contains(DeviceList[i]))
                {
                    DeviceList.RemoveAt(i);
                }
            }

            for (var i = 0; i < target.Count; i++)
            {
                if (i < DeviceList.Count && ReferenceEquals(DeviceList[i], target[i]))
                {
                    continue;
                }

                var existingIndex = DeviceList.IndexOf(target[i]);
                if (existingIndex >= 0)
                {
                    DeviceList.Move(existingIndex, i);
                }
                else
                {
                    DeviceList.Insert(i, target[i]);
                }
            }

            UpdateDeviceCount();
        }

        private void UpdateDeviceCount()
        {
            var hasDevices = DeviceList.Count > 0;
            devicesList.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
            emptyStatePanel.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;

            if (!hasDevices)
            {
                deviceCountText.Text = string.IsNullOrWhiteSpace(_filterText)
                    ? "No devices found yet"
                    : "No devices match your search";
                return;
            }

            deviceCountText.Text = DeviceList.Count == 1
                ? "1 device found"
                : $"{DeviceList.Count} devices found";
        }

        /// <summary>
        /// macOS refuses AirPlay connections until the receiver is opened up, and Macs
        /// that still advertise classic AirPlay hit the same wall, so this shows up front
        /// rather than after a failed attempt. It stays dismissed once closed.
        /// </summary>
        private void RefreshAirPlayReceiverHint()
        {
            if (_settings.Settings.AirPlayReceiverHintDismissed)
            {
                macHintBar.IsOpen = false;
                return;
            }

            macHintBar.IsOpen = _allDevices.Count > 0;
        }

        private void MacHintBar_CloseButtonClick(InfoBar sender, object args)
        {
            _settings.Update(settings => settings.AirPlayReceiverHintDismissed = true);
            AppLog.Info("ui", "AirPlay receiver hint dismissed.");
        }

        private void OnStreamingStateChanged()
        {
            SyncConnectionState();
            RefreshSessionStatus();
        }

        private void SyncConnectionState()
        {
            var connected = _streamingOrchestrator.ConnectedReceivers;
            foreach (var device in _allDevices)
            {
                device.IsConnected = connected.Any(receiver =>
                    ReceiverKey.SameReceiver(receiver, device.Device));
            }
        }

        private void RefreshSessionStatus()
        {
            var connectedCount = _streamingOrchestrator.ConnectedReceivers.Count;
            var (text, brushKey) = _streamingOrchestrator.State switch
            {
                SessionState.Connecting => ("Connecting…", "SystemFillColorCautionBrush"),
                SessionState.Reconnecting => ("Reconnecting…", "SystemFillColorCautionBrush"),
                SessionState.Streaming => (
                    connectedCount == 1 ? "Streaming" : $"Streaming to {connectedCount} devices",
                    "SystemFillColorSuccessBrush"),
                SessionState.Degraded => ("Streaming (degraded)", "SystemFillColorCautionBrush"),
                SessionState.Disconnecting => ("Disconnecting…", "TextFillColorSecondaryBrush"),
                SessionState.Failed => ("Stream failed", "SystemFillColorCriticalBrush"),
                _ => ("Not connected", "TextFillColorSecondaryBrush")
            };

            statusPillText.Text = text;
            statusDot.Fill = ThemeBrush(brushKey);
        }

        private void ShowMessage(InfoBarSeverity severity, string title, string message)
        {
            messageBar.Severity = severity;
            messageBar.Title = title;
            messageBar.Message = message;
            messageBar.IsOpen = true;
        }

        private async void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: DeviceViewModel device })
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = device.DisplayName,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = CreateDeviceSummary(device.Device),
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    },
                    VerticalScrollMode = ScrollMode.Auto,
                    HorizontalScrollMode = ScrollMode.Disabled
                },
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot
            };

            await dialog.ShowAsync();
        }

        private static string CreateDeviceSummary(DeviceInfo device)
        {
            var protocol = AirPlayCapability.DescribePreferred(device);

            return $"Model: {Fallback(device.Model)}\n" +
                   $"Manufacturer: {Fallback(device.Manufacturer)}\n" +
                   $"Address: {Fallback(device.IPAddress)}:{device.Port}\n" +
                   $"Protocol: {protocol}\n" +
                   $"Encryption: {Fallback(device.EncryptionTypes)}";
        }

        private static string Fallback(string value) =>
            string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

        private static string FormatConnectionFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "Unknown error.";
            }

            if (message.Contains("Everyone", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("same network", StringComparison.OrdinalIgnoreCase))
            {
                return message;
            }

            if (message.Contains("470", StringComparison.Ordinal) ||
                message.Contains("403", StringComparison.Ordinal) ||
                message.Contains("Pairing", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("pair-setup", StringComparison.OrdinalIgnoreCase))
            {
                return message +
                    " On a Mac, open System Settings > General > AirDrop & Handoff, set " +
                    "AirPlay Receiver to \"Everyone\" or \"Anyone on the same network\", " +
                    "and turn off the required password.";
            }

            return message;
        }

        private void UpdateUI(bool isEnabled)
        {
            searchButton.IsEnabled = isEnabled;
            refreshButton.IsEnabled = isEnabled;
        }

        private static Brush ThemeBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Colors.Gray);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
