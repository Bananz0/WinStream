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
using WinStream.Core;
using WinStream.Core.Audio;
using WinStream.Core.Drivers;
using WinStream.Core.Logging;
using WinStream.Core.Network;
using WinStream.Core.Persistence;
using WinStream.Core.Streaming;
using WinStream.Core.Streaming.Link;
using WinStream.Network;
using WinStream.Streaming;
using WinStream.Tray;
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
        private readonly LinkCredentialStore _linkCredentials = new();
        private readonly LinkConnectionCoordinator _link;
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
        private bool _suppressLaunchAtStartupEvents;
        private bool _suppressStreamingQualityEvents;
        private bool _suppressSinkModeEvents;
        private bool _suppressLinkDiscoveryEvents;
        private readonly QualityApplyGate _qualityApplyGate = new();
        private readonly SinkModeCoordinator _sinkModes;

        public MainWindow()
        {
            InitializeComponent();
            _captureMonitor = new CaptureMonitorService(_settings);
            _streamingOrchestrator = new StreamingOrchestrator(_settings.EnsureSenderDeviceId());
            _link = LinkCoordinatorFactory.Create(_linkCredentials);
            _sinkModes = new SinkModeCoordinator(
                ct => _streamingOrchestrator.DisconnectAsync(cancellationToken: ct),
                _ => StopLinkAsync());
            _streamingOrchestrator.SetPairingPinPrompt(async ct =>
            {
                var pin = await PromptForAirPlayPinAsync(ct);
                return string.IsNullOrWhiteSpace(pin) ? null : pin;
            });
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
            RestoreStreamingQualitySettings();
            RestoreLinkSinkSettings();
            _ = RestoreLaunchAtStartupSettingAsync();
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

        /// <summary>Builds the live tray context-menu snapshot (devices + connect/disconnect affordances).</summary>
        internal TrayMenuState BuildTrayMenuState()
        {
            var settings = _settings.Settings;
            var airPlayMode = settings.SinkMode == SinkMode.AirPlay;
            var lastKey = settings.LastReceiverKey;
            var lastName = settings.LastReceiverName;
            var lastRow = string.IsNullOrWhiteSpace(lastKey)
                ? null
                : _allDevices.FirstOrDefault(device =>
                    string.Equals(device.Key, lastKey, StringComparison.Ordinal));

            // Enabled whenever we remember a device and aren't already on it — even if it's
            // offline right now — so the tray action can rescan and report a clear miss.
            var canConnectLast = !string.IsNullOrWhiteSpace(lastKey)
                && lastRow?.IsConnected != true
                && !_connectionInFlight
                && airPlayMode
                && (lastRow is null || lastRow.IsActionEnabled);

            var connectedCount = _streamingOrchestrator.ConnectedReceivers.Count;
            var linkConnected = _link.State == LinkSessionState.Streaming;
            var devices = _allDevices
                .Select(device => new TrayDeviceItem
                {
                    Key = device.Key,
                    DisplayName = device.DisplayName,
                    IsConnected = device.IsConnected,
                    IsEnabled = airPlayMode && device.IsActionEnabled && !_connectionInFlight
                })
                .ToList();

            return new TrayMenuState
            {
                LastReceiverName = string.IsNullOrWhiteSpace(lastName) ? null : lastName,
                CanConnectLast = canConnectLast,
                CanDisconnect = (connectedCount > 0 || linkConnected) && !_connectionInFlight,
                ConnectedCount = connectedCount + (linkConnected ? 1 : 0),
                Devices = devices
            };
        }

        /// <summary>Tray: connect to the remembered receiver (rescans first if needed).</summary>
        internal async Task ConnectLastFromTrayAsync()
        {
            if (_settings.Settings.SinkMode != SinkMode.AirPlay)
            {
                return;
            }

            var key = _settings.Settings.LastReceiverKey;
            var name = _settings.Settings.LastReceiverName;
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (_connectionInFlight)
            {
                return;
            }

            var row = FindDeviceRow(key);
            if (row is null)
            {
                await DiscoverAndDisplayDevicesAsync();
                row = FindDeviceRow(key);
            }

            if (row is null)
            {
                ShowFromTray();
                ShowMessage(
                    InfoBarSeverity.Warning,
                    name is null ? "Last device not found" : $"{name} not found",
                    "That speaker isn't on the network right now. Discover from settings when it's powered on.");
                return;
            }

            if (row.IsConnected)
            {
                return;
            }

            await SetDeviceConnectionAsync(row, connect: true);
        }

        /// <summary>Tray: connect a discovered receiver by stable key.</summary>
        internal async Task ConnectDeviceFromTrayAsync(string deviceKey)
        {
            if (_settings.Settings.SinkMode != SinkMode.AirPlay ||
                string.IsNullOrWhiteSpace(deviceKey) ||
                _connectionInFlight)
            {
                return;
            }

            var row = FindDeviceRow(deviceKey);
            if (row is null)
            {
                await DiscoverAndDisplayDevicesAsync();
                row = FindDeviceRow(deviceKey);
            }

            if (row is null)
            {
                ShowFromTray();
                ShowMessage(
                    InfoBarSeverity.Warning,
                    "Device not found",
                    "That speaker dropped off the network. Try Discover from settings.");
                return;
            }

            if (row.IsConnected)
            {
                return;
            }

            await SetDeviceConnectionAsync(row, connect: true);
        }

        /// <summary>Tray: disconnect every active receiver.</summary>
        internal async Task DisconnectFromTrayAsync()
        {
            var linkConnected = _link.State == LinkSessionState.Streaming;
            if (_connectionInFlight ||
                (_streamingOrchestrator.ConnectedReceivers.Count == 0 && !linkConnected))
            {
                return;
            }

            _connectionInFlight = true;
            UpdateUI(false);
            try
            {
                await StopLinkAsync();
                await _streamingOrchestrator.DisconnectAsync();
                foreach (var device in _allDevices)
                {
                    if (device.IsConnected)
                    {
                        device.IsConnected = false;
                        device.SetStatus("Disconnected.", DeviceStatusKind.Neutral);
                    }
                }

                await StopCaptureIfIdleAsync();
                AppLog.Info("ui", "Disconnected all receivers from tray.");
            }
            catch (Exception ex)
            {
                ShowFromTray();
                ShowMessage(
                    InfoBarSeverity.Error,
                    "Couldn't disconnect",
                    FormatConnectionFailure(ex.Message));
                AppLog.Error("ui", $"Tray disconnect error: {ex.GetType().Name}");
            }
            finally
            {
                _connectionInFlight = false;
                UpdateUI(true);
                SyncConnectionState();
                RefreshSessionStatus();
            }

            await DrainPendingQualityApplyAsync();
        }

        public async Task CloseForExitAsync()
        {
            _allowClose = true;
            _scanTimer.Stop();
            _captureLevelTimer.Stop();

            // Tear down streaming first: the orchestrator is still subscribed to the
            // capture source and would pump frames into a disposed WASAPI client.
            await _link.DisposeAsync();
            linkStatusText.Text = "Link disconnected.";
            await _streamingOrchestrator.DisposeAsync();
            await _captureMonitor.DisposeAsync();
            _driverLifecycle.Dispose();
            Close();
        }

        private DeviceViewModel FindDeviceRow(string key) =>
            _allDevices.FirstOrDefault(device =>
                string.Equals(device.Key, key, StringComparison.Ordinal));

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

        private void RestoreLinkSinkSettings()
        {
            var enabled = _settings.Settings.LinkFeatureEnabled;
            linkSinkCard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
            {
                return;
            }

            _suppressSinkModeEvents = true;
            try
            {
                sinkModeComboBox.Items.Clear();
                sinkModeComboBox.Items.Add(new ComboBoxItem
                {
                    Content = "AirPlay speakers",
                    Tag = SinkMode.AirPlay
                });
                sinkModeComboBox.Items.Add(new ComboBoxItem
                {
                    Content = "WinStream Link companion",
                    Tag = SinkMode.Link
                });
                sinkModeComboBox.SelectedIndex =
                    _settings.Settings.SinkMode == SinkMode.Link ? 1 : 0;
                ApplySinkModeUi(_settings.Settings.SinkMode);
            }
            finally
            {
                _suppressSinkModeEvents = false;
            }
        }

        private void ApplySinkModeUi(SinkMode mode)
        {
            var link = mode == SinkMode.Link;
            linkConnectPanel.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
            streamingQualityCard.Visibility = link ? Visibility.Collapsed : Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(_settings.Settings.LastLinkReceiverKey) &&
                string.IsNullOrWhiteSpace(linkHostTextBox.Text))
            {
                var key = _settings.Settings.LastLinkReceiverKey;
                var host = key.Contains(':') ? key.Split(':')[0] : key;
                linkHostTextBox.Text = host;
                if (_linkCredentials.TryGetPin(key, out var savedPin))
                {
                    linkPinBox.Password = savedPin;
                }
            }
        }

        private async Task StopLinkAsync()
        {
            await _link.DisconnectAsync();
            linkStatusText.Text = "Link disconnected.";
        }

        private async void SinkModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSinkModeEvents ||
                sinkModeComboBox.SelectedItem is not ComboBoxItem { Tag: SinkMode next })
            {
                return;
            }

            var previous = _settings.Settings.SinkMode;
            if (previous == next)
            {
                ApplySinkModeUi(next);
                return;
            }

            if (SinkModeSwitchPolicy.RequiresTeardown(previous, next))
            {
                var confirm = new ContentDialog
                {
                    Title = "Switch sink mode?",
                    Content = SinkModeSwitchPolicy.ConfirmMessage(previous, next),
                    PrimaryButtonText = "Switch",
                    CloseButtonText = "Cancel",
                    XamlRoot = Content.XamlRoot
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    _suppressSinkModeEvents = true;
                    try
                    {
                        sinkModeComboBox.SelectedIndex = previous == SinkMode.Link ? 1 : 0;
                    }
                    finally
                    {
                        _suppressSinkModeEvents = false;
                    }

                    return;
                }

                await _sinkModes.PrepareSwitchAsync(previous, next);
            }

            _settings.Update(s => s.SinkMode = next);
            ApplySinkModeUi(next);
            RefreshSessionStatus();
        }

        private async void LinkScanButton_Click(object sender, RoutedEventArgs e)
        {
            linkScanButton.IsEnabled = false;
            linkStatusText.Text = "Scanning for Link companions…";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var found = await LinkDeviceDiscovery.DiscoverAsync(timeout.Token);

                _suppressLinkDiscoveryEvents = true;
                try
                {
                    linkDiscoveryComboBox.Items.Clear();
                    foreach (var device in found.Where(device =>
                        !string.IsNullOrWhiteSpace(device.IPAddress)))
                    {
                        linkDiscoveryComboBox.Items.Add(new ComboBoxItem
                        {
                            Content = $"{device.DisplayName} ({device.Key})",
                            Tag = device.Key
                        });
                    }
                }
                finally
                {
                    _suppressLinkDiscoveryEvents = false;
                }

                linkStatusText.Text = linkDiscoveryComboBox.Items.Count == 0
                    ? "No companions advertised; enter the IP manually."
                    : $"Found {linkDiscoveryComboBox.Items.Count} companion(s).";
            }
            catch (Exception ex)
            {
                linkStatusText.Text = $"Link scan failed: {ex.Message}";
                AppLog.Error("link", $"Link discovery failed: {ex.GetType().Name}");
            }
            finally
            {
                linkScanButton.IsEnabled = true;
            }
        }

        private void LinkDiscoveryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLinkDiscoveryEvents ||
                linkDiscoveryComboBox.SelectedItem is not ComboBoxItem { Tag: string key })
            {
                return;
            }

            linkHostTextBox.Text = key;
            if (_linkCredentials.TryGetPin(key, out var savedPin))
            {
                linkPinBox.Password = savedPin;
            }
        }

        private async void LinkConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_settings.Settings.SinkMode != SinkMode.Link)
            {
                linkStatusText.Text = "Switch sink mode to WinStream Link first.";
                return;
            }

            linkConnectButton.IsEnabled = false;
            linkStatusText.Text = "Connecting Link…";
            try
            {
                await _sinkModes.EnsureExclusiveAsync(SinkMode.Link);
                await _captureMonitor.StopAsync();

                var result = await _link.ConnectAsync(linkHostTextBox.Text, linkPinBox.Password);
                if (!result.IsConnected)
                {
                    linkStatusText.Text = DescribeLinkFailure(result);
                    return;
                }

                _settings.Update(s =>
                {
                    s.LastLinkReceiverKey = result.Target!.Key;
                    s.LastLinkReceiverName = result.Target.Host;
                });
                linkStatusText.Text = DescribeLinkCapture();
            }
            catch (Exception ex)
            {
                linkStatusText.Text = $"Link failed: {ex.Message}";
                AppLog.Error("link", $"UI connect failed: {ex}");
            }
            finally
            {
                linkConnectButton.IsEnabled = true;
            }
        }

        private static string DescribeLinkFailure(LinkConnectResult result) => result.Status switch
        {
            LinkConnectStatus.MissingPin => "Enter the Link PIN shown by the companion.",
            LinkConnectStatus.InvalidTarget => "Enter the companion address as IP or IP:port.",
            LinkConnectStatus.PinRejected => "Link PIN rejected by companion.",
            LinkConnectStatus.CaptureFailed => $"Link capture failed: {result.Detail}",
            _ => $"Link failed: {result.Detail}"
        };

        /// <summary>Never claim the 8–10 ms path without an owned VAD and measured callbacks.</summary>
        private string DescribeLinkCapture()
        {
            var measuredMs = _link.MeasuredCaptureContributionMilliseconds;
            return _link.CaptureQuality switch
            {
                LinkCaptureQuality.LegacyLoopback =>
                    "Link streaming through default loopback (SLA-ineligible).",
                LinkCaptureQuality.VadMeasuring =>
                    "Link streaming through WinStream VAD; measuring capture timing…",
                LinkCaptureQuality.VadWithinBudget =>
                    $"Link streaming through VAD (capture p95 {measuredMs} ms; " +
                    "Ethernet E2E measurement still required).",
                LinkCaptureQuality.VadOverBudget =>
                    $"Link streaming through VAD (capture p95 {measuredMs} ms exceeds the " +
                    $"{LinkSlaEligibility.MaxCaptureContributionMs} ms budget).",
                _ => "Link not connected."
            };
        }

        private void RestoreStreamingQualitySettings()
        {
            _suppressStreamingQualityEvents = true;
            try
            {
                playbackResponsivenessComboBox.Items.Clear();
                // Auto first, then fixed presets shortest → longest delay.
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.Auto,
                    "Auto (recommended)",
                    "Starts near ~250 ms and increases toward ~2 s if delivery pressure is detected. Not a guaranteed delay."));
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.LabPacket,
                    StreamingQualityCopy.ExtremeLabel,
                    StreamingQualityCopy.ExtremeHint));
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.Experimental,
                    "Experimental (~250 ms)",
                    "Fixed ~250 ms buffer. Expect stutter or tear-down on some receivers."));
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.VeryLow,
                    "Very low (~500 ms)",
                    "Fixed ~500 ms buffer. More stutter risk than Low delay on busy Wi‑Fi."));
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.LowDelay,
                    "Low delay (~1 s)",
                    "Snappier playback. More likely to stutter on busy Wi‑Fi."));
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.Balanced,
                    "Balanced (~1.5 s)",
                    "Fixed 1.5-second buffer. Similar to other compatible AirPlay senders."));
                playbackResponsivenessComboBox.Items.Add(CreateQualityOption(
                    PlaybackResponsiveness.MostStable,
                    "Most stable (~2 s)",
                    "Apple-standard realtime buffer for the strongest dropout protection."));

                SelectQualityOption(playbackResponsivenessComboBox, _settings.Settings.PlaybackResponsiveness);
                audioFidelityComboBox.Items.Clear();
                audioFidelityComboBox.Items.Add(CreateQualityOption(
                    AudioFidelity.Auto,
                    "Auto",
                    "Skips conversion when Windows already matches AirPlay (44.1 kHz stereo)."));
                audioFidelityComboBox.Items.Add(CreateQualityOption(
                    AudioFidelity.Standard,
                    "Standard",
                    StreamingQualityCopy.StandardFidelityHint));
                audioFidelityComboBox.Items.Add(CreateQualityOption(
                    AudioFidelity.HighFidelity,
                    "High fidelity",
                    StreamingQualityCopy.HighFidelityHint));

                SelectQualityOption(audioFidelityComboBox, _settings.Settings.AudioFidelity);
                RefreshStreamingQualityHints();
            }
            finally
            {
                _suppressStreamingQualityEvents = false;
            }
        }

        private static ComboBoxItem CreateQualityOption<T>(T value, string title, string description)
            where T : struct, Enum
        {
            var item = new ComboBoxItem
            {
                Content = title,
                Tag = value
            };
            ToolTipService.SetToolTip(item, description);
            return item;
        }

        private static void SelectQualityOption<T>(ComboBox comboBox, T value)
            where T : struct, Enum
        {
            foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is T tagged && EqualityComparer<T>.Default.Equals(tagged, value))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            if (comboBox.Items.Count > 0)
            {
                comboBox.SelectedIndex = 0;
            }
        }

        private void RefreshStreamingQualityHints()
        {
            playbackResponsivenessHintText.Text =
                playbackResponsivenessComboBox.SelectedItem is ComboBoxItem responsiveness &&
                ToolTipService.GetToolTip(responsiveness) is string responsivenessHint
                    ? responsivenessHint
                    : string.Empty;
            audioFidelityHintText.Text =
                audioFidelityComboBox.SelectedItem is ComboBoxItem fidelity &&
                ToolTipService.GetToolTip(fidelity) is string fidelityHint
                    ? fidelityHint
                    : string.Empty;
        }

        /// <summary>
        /// Reads the tag of a quality combo, honouring the suppress flag used while the
        /// combos are rebuilt from settings.
        /// </summary>
        private bool TryReadQualitySelection<T>(ComboBox comboBox, out T mode)
            where T : struct, Enum
        {
            mode = default;
            if (_suppressStreamingQualityEvents ||
                comboBox.SelectedItem is not ComboBoxItem item ||
                item.Tag is not T tagged)
            {
                return false;
            }

            mode = tagged;
            return true;
        }

        private void SelectQualityOptionSilently<T>(ComboBox comboBox, T value)
            where T : struct, Enum
        {
            _suppressStreamingQualityEvents = true;
            try
            {
                SelectQualityOption(comboBox, value);
                RefreshStreamingQualityHints();
            }
            finally
            {
                _suppressStreamingQualityEvents = false;
            }
        }

        private async void PlaybackResponsivenessComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!TryReadQualitySelection<PlaybackResponsiveness>(
                    playbackResponsivenessComboBox,
                    out var mode))
            {
                return;
            }

            var previous = _settings.Settings.PlaybackResponsiveness;
            if (mode == previous)
            {
                RefreshStreamingQualityHints();
                return;
            }

            _settings.Update(settings => settings.PlaybackResponsiveness = mode);
            RefreshStreamingQualityHints();

            // Settings are written first so the apply reads one source of truth, but a
            // refused preset must not stay committed: the Lab single-receiver guard reads
            // the orchestrator's copy, which only advances on success.
            await ApplyStreamingQualityNowAsync(
                "Playback responsiveness",
                () =>
                {
                    _settings.Update(settings => settings.PlaybackResponsiveness = previous);
                    SelectQualityOptionSilently(playbackResponsivenessComboBox, previous);
                });
        }

        private void AudioFidelityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!TryReadQualitySelection<AudioFidelity>(audioFidelityComboBox, out var mode))
            {
                return;
            }

            if (mode == _settings.Settings.AudioFidelity)
            {
                RefreshStreamingQualityHints();
                return;
            }

            _settings.Update(settings => settings.AudioFidelity = mode);
            RefreshStreamingQualityHints();

            // Fidelity is a converter setting, not a SETUP parameter, so it takes effect
            // on the live session without the tear-down that responsiveness needs.
            _streamingOrchestrator.SetAudioFidelity(mode);
        }

        /// <summary>
        /// Collects the AirPlay code shown on the Mac during first-time persistent
        /// pairing. Cancel falls back to transient pairing (Accept prompt every time).
        /// </summary>
        private Task<string> PromptForAirPlayPinAsync(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            tcs.TrySetResult(string.Empty);
                            return;
                        }

                        // The receiver shows a 4-digit code, but a Mac with an AirPlay
                        // Receiver password expects that password instead — both are the
                        // same SRP secret, so the field must not be digits-only.
                        var pinBox = new TextBox
                        {
                            PlaceholderText = "AirPlay code or password"
                        };
                        AutomationProperties.SetName(pinBox, "AirPlay pairing code or password");

                        var dialog = new ContentDialog
                        {
                            Title = "Enter AirPlay code",
                            Content = new StackPanel
                            {
                                Spacing = 12,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = StreamingQualityCopy.PairingPromptBody,
                                        TextWrapping = TextWrapping.WrapWholeWords
                                    },
                                    pinBox
                                }
                            },
                            PrimaryButtonText = "Pair",
                            CloseButtonText = "Skip",
                            DefaultButton = ContentDialogButton.Primary,
                            XamlRoot = Content.XamlRoot
                        };

                        using var reg = cancellationToken.Register(() =>
                        {
                            dialog.Hide();
                            tcs.TrySetResult(string.Empty);
                        });

                        var result = await dialog.ShowAsync();
                        if (result != ContentDialogResult.Primary)
                        {
                            tcs.TrySetResult(string.Empty);
                            return;
                        }

                        tcs.TrySetResult(pinBox.Text?.Trim() ?? string.Empty);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                }))
            {
                tcs.TrySetResult(string.Empty);
            }

            return tcs.Task;
        }

        private async Task<bool> OfferLabEscapeToExperimentalAsync()
        {
            if (_settings.Settings.PlaybackResponsiveness != PlaybackResponsiveness.LabPacket)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                Title = "Extreme delay failed",
                Content = StreamingQualityCopy.LabEscapeBody,
                PrimaryButtonText = "Use Experimental",
                CloseButtonText = "Keep Extreme",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return false;
            }

            _settings.Update(settings =>
                settings.PlaybackResponsiveness = PlaybackResponsiveness.Experimental);
            _suppressStreamingQualityEvents = true;
            try
            {
                SelectQualityOption(
                    playbackResponsivenessComboBox,
                    PlaybackResponsiveness.Experimental);
                RefreshStreamingQualityHints();
            }
            finally
            {
                _suppressStreamingQualityEvents = false;
            }

            return true;
        }

        /// <summary>
        /// Settings are already saved. AirPlay negotiates latency at SETUP, so a live
        /// session is restarted in place — silently, with no confirmation.
        /// </summary>
        private async Task ApplyStreamingQualityNowAsync(string settingName, Action onFailed)
        {
            if (!_qualityApplyGate.TryBegin(_connectionInFlight))
            {
                AppLog.Info("ui", $"Deferred {settingName} until the current session change finishes.");
                return;
            }

            // Same gate as connect/disconnect: the tray and device list must not start
            // a session while the aggregate is being torn down and rebuilt.
            _connectionInFlight = true;
            UpdateUI(false);
            try
            {
                do
                {
                    await _streamingOrchestrator.ApplyStreamingQualityAsync(
                        _settings.Settings.PlaybackResponsiveness,
                        _settings.Settings.AudioFidelity);
                }
                while (_qualityApplyGate.ShouldRepeat());
            }
            catch (Exception ex)
            {
                _qualityApplyGate.Clear();
                onFailed();
                AppLog.Error("ui", $"Applying {settingName} failed: {ex.GetType().Name}");
                ShowMessage(
                    InfoBarSeverity.Error,
                    $"Couldn't apply {settingName.ToLowerInvariant()}",
                    FormatConnectionFailure(ex.Message));
            }
            finally
            {
                _connectionInFlight = false;
                UpdateUI(true);
                SyncConnectionState();
                RefreshSessionStatus();
            }
        }

        /// <summary>
        /// Replays a preset change that arrived while a connect or disconnect held
        /// <see cref="_connectionInFlight"/>.
        /// </summary>
        private Task DrainPendingQualityApplyAsync()
        {
            if (!_qualityApplyGate.HasPending || _connectionInFlight)
            {
                return Task.CompletedTask;
            }

            // The replay re-reads settings, so it always targets the newest preset. A
            // failure here reverts the combo to whatever the orchestrator accepted last.
            var restore = _streamingOrchestrator.Responsiveness;
            return ApplyStreamingQualityNowAsync(
                "Playback responsiveness",
                () =>
                {
                    _settings.Update(settings => settings.PlaybackResponsiveness = restore);
                    SelectQualityOptionSilently(playbackResponsivenessComboBox, restore);
                });
        }

        private async void PlaybackResponsivenessInfo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Playback responsiveness",
                Content = StreamingQualityCopy.ResponsivenessInfoBody,
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async void AudioFidelityInfo_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "Audio fidelity",
                Content =
                    "AirPlay uses lossless ALAC in every mode. This setting mainly affects sample-rate conversion when Windows audio is not already 44.1 kHz stereo.\n\n" +
                    "• Auto — Skips conversion when already 44.1 kHz stereo; otherwise linear.\n" +
                    "• Standard — Same conversion as Auto today; reserved for a lighter path later.\n" +
                    "• High fidelity — Reserved for richer conversion; today matches Auto.\n\n" +
                    "It does not change the playback buffer.",
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
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
                ? StreamingQualityCopy.AutoConnectOnDescription(receiverName)
                : $"Your last device was {receiverName}. Turn this on to reconnect to it automatically.";
        }

        private async Task RestoreLaunchAtStartupSettingAsync()
        {
            _suppressLaunchAtStartupEvents = true;

            // A click landing mid-restore would be swallowed by the suppression flag and then
            // snapped back by the snapshot, so keep the switch inert until Windows answers.
            launchAtStartupToggle.IsEnabled = false;
            try
            {
                var snapshot = await StartupRegistration.ReconcileAsync(
                    _settings.Settings.LaunchAtStartup);
                ApplyStartupSnapshot(snapshot);

                // Only let Windows overwrite the saved preference when it owns the decision.
                // Otherwise a registration that vanished (reinstall, packaged vs unpackaged run)
                // would erase the user's intent and stop it from being re-applied next launch.
                if (snapshot.IsEnabled || !snapshot.CanToggle)
                {
                    _settings.Update(settings => settings.LaunchAtStartup = snapshot.IsEnabled);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("startup", $"Startup registration unavailable: {ex.Message}");
                launchAtStartupToggle.IsOn = _settings.Settings.LaunchAtStartup;
                launchAtStartupToggle.IsEnabled = true;
                launchAtStartupDescriptionText.Text =
                    "Start WinStream in the tray after Windows login.";
            }
            finally
            {
                _suppressLaunchAtStartupEvents = false;
            }
        }

        private void ApplyStartupSnapshot(StartupRegistrationSnapshot snapshot)
        {
            launchAtStartupToggle.IsOn = snapshot.IsEnabled;
            launchAtStartupToggle.IsEnabled = snapshot.CanToggle;
            launchAtStartupDescriptionText.Text = snapshot.StatusMessage;
        }

        private async void LaunchAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressLaunchAtStartupEvents)
            {
                return;
            }

            var wantEnabled = launchAtStartupToggle.IsOn;
            _suppressLaunchAtStartupEvents = true;
            try
            {
                var snapshot = await StartupRegistration.SetEnabledAsync(wantEnabled);
                ApplyStartupSnapshot(snapshot);

                // Remember what the user asked for, not just what Windows granted, so the next
                // launch can re-apply it if the registration goes missing.
                _settings.Update(settings => settings.LaunchAtStartup =
                    snapshot.CanToggle ? wantEnabled : snapshot.IsEnabled);

                if (wantEnabled && !snapshot.IsEnabled)
                {
                    ShowMessage(
                        InfoBarSeverity.Warning,
                        "Startup blocked by Windows",
                        "Open Settings > Apps > Startup, enable WinStream, then try again.");
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("startup", $"Failed to update startup registration: {ex.Message}");
                launchAtStartupToggle.IsOn = !wantEnabled;
                ShowMessage(
                    InfoBarSeverity.Error,
                    "Could not change startup",
                    ex.Message);
            }
            finally
            {
                _suppressLaunchAtStartupEvents = false;
            }
        }

        private void UpdateCaptureLevelUi()
        {
            if (_settings.Settings.SinkMode == SinkMode.Link &&
                _link.State == LinkSessionState.Streaming)
            {
                linkStatusText.Text = DescribeLinkCapture();
            }

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

            // Push unconditionally: the orchestrator keeps the last value and replays it
            // onto replacement sessions, so skipping non-Streaming states would let a
            // reconnect restore a stale level.
            await _streamingOrchestrator.SetVolumeAsync(PercentToDb(e.NewValue));
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
            if (connect && _settings.Settings.SinkMode != SinkMode.AirPlay)
            {
                if (!isAutomatic)
                {
                    ShowMessage(
                        InfoBarSeverity.Warning,
                        "WinStream Link is active",
                        "Switch Sink mode to AirPlay speakers before connecting an AirPlay receiver.");
                }

                return;
            }

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

            var reconnectAfterLabEscape = false;
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

                _streamingOrchestrator.ConfigureStreamingQuality(
                    _settings.Settings.PlaybackResponsiveness,
                    _settings.Settings.AudioFidelity);
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
                    // Tray-initiated connects can fail while the window is hidden.
                    if (!_appWindow.IsVisible)
                    {
                        ShowFromTray();
                    }

                    ShowMessage(
                        InfoBarSeverity.Error,
                        $"Couldn't connect to {device.DisplayName}",
                        FormatConnectionFailure(ex.Message));

                    if (_settings.Settings.PlaybackResponsiveness == PlaybackResponsiveness.LabPacket)
                    {
                        reconnectAfterLabEscape = await OfferLabEscapeToExperimentalAsync();
                    }
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

            if (reconnectAfterLabEscape)
            {
                await SetDeviceConnectionAsync(device, connect: true);
            }

            await DrainPendingQualityApplyAsync();
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
            if (!SinkModeSwitchPolicy.AllowsAirPlayAutoConnect(settings.SinkMode))
            {
                return;
            }

            if (!AutoConnectPolicy.ShouldAttempt(
                    settings.AutoConnectLastReceiver,
                    settings.LastReceiverKey,
                    _streamingOrchestrator.State,
                    _connectionInFlight,
                    _autoConnectAttempts.AttemptsAvailable,
                    settings.PlaybackResponsiveness))
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
            if (_settings.Settings.SinkMode == SinkMode.Link)
            {
                var linkState = _link.State;
                var (linkText, linkBrushKey) = linkState switch
                {
                    LinkSessionState.Streaming => ("Link streaming", "SystemFillColorSuccessBrush"),
                    LinkSessionState.Failed => ("Link failed", "SystemFillColorCriticalBrush"),
                    _ => ("Link not connected", "TextFillColorSecondaryBrush")
                };
                statusPillText.Text = linkText;
                statusDot.Fill = ThemeBrush(linkBrushKey);
                return;
            }

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
            // The presets drive SETUP, so they must read as busy while a session is
            // being negotiated — otherwise a change looks ignored until it replays.
            playbackResponsivenessComboBox.IsEnabled = isEnabled;
            audioFidelityComboBox.IsEnabled = isEnabled;
        }

        private static Brush ThemeBrush(string key) =>
            Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
                ? brush
                : new SolidColorBrush(Colors.Gray);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
