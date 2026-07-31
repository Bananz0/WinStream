using Microsoft.UI.Xaml;
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
using WinStream.Core.Logging;
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
        private readonly CaptureMonitorService _captureMonitor = new();
        private readonly StreamingOrchestrator _streamingOrchestrator = new();
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _captureLevelTimer;
        private readonly AppWindow _appWindow;
        private string _filterText = string.Empty;
        private bool _allowClose;
        private bool _isScanning;
        private bool _suppressCaptureSelectionEvents;
        private bool _suppressAutoConnectEvents;
        private bool _autoConnectAttempted;

        public MainWindow()
        {
            InitializeComponent();
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
            _streamingOrchestrator.StateChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(OnStreamingStateChanged);

            LoadCaptureEndpoints();
            RestoreCaptureSettings();
            RestoreAutoConnectSetting();
            captureModeComboBox.SelectedIndex =
                _captureMonitor.Settings.CaptureMode == CaptureMode.VirtualDriver ? 1 : 0;
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
            await _captureMonitor.DisposeAsync();
            await _streamingOrchestrator.DisposeAsync();
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

                var selectedId = _captureMonitor.Settings.SelectedRenderDeviceId;
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
                monitorCaptureToggle.IsOn = _captureMonitor.Settings.MonitorCapture;
            }
            finally
            {
                _suppressCaptureSelectionEvents = false;
            }

            if (_captureMonitor.Settings.MonitorCapture)
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
                autoConnectToggle.IsOn = _captureMonitor.Settings.AutoConnectLastReceiver;
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

            _captureMonitor.SetAutoConnectLastReceiver(autoConnectToggle.IsOn);
            _autoConnectAttempted = false;
            RefreshAutoConnectDescription();

            if (autoConnectToggle.IsOn)
            {
                await TryAutoConnectToLastReceiverAsync();
            }
        }

        private void RefreshAutoConnectDescription()
        {
            var receiverName = _captureMonitor.Settings.LastReceiverName;
            autoConnectDescriptionText.Text = string.IsNullOrWhiteSpace(receiverName)
                ? "Your next successful connection will become the startup device."
                : $"Automatically reconnect to {receiverName} as soon as it appears.";
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

        private void CaptureModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (captureModeComboBox.SelectedItem is ComboBoxItem item &&
                item.Tag is string tag &&
                Enum.TryParse<CaptureMode>(tag, out var mode))
            {
                _captureMonitor.SetCaptureMode(mode);
                if (mode == CaptureMode.VirtualDriver)
                {
                    ShowMessage(
                        InfoBarSeverity.Warning,
                        "Virtual audio driver required",
                        "Install the optional WinStream audio driver to use this mode. " +
                        "It isn't included in the Store version.");
                }
            }
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

        private async Task SetDeviceConnectionAsync(
            DeviceViewModel device,
            bool connect,
            bool isAutomatic = false)
        {
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
                    return;
                }

                AppLog.Info("ui", "Connecting to selected receiver.");
                await _captureMonitor.EnsureStartedAsync();
                var source = _captureMonitor.GetSourceForStreaming()
                    ?? throw new InvalidOperationException(
                        "Windows didn't provide an audio source to capture.");

                await _streamingOrchestrator.ConnectAsync(device.Device, source);
                await _streamingOrchestrator.SetVolumeAsync(PercentToDb(streamVolumeSlider.Value));
                _captureMonitor.RememberReceiver(device.Key, device.DisplayName);
                RefreshAutoConnectDescription();

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
                device.SetStatus(
                    isAutomatic
                        ? "Automatic connection failed. Connect manually to try again."
                        : "Couldn't connect.",
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
                device.IsBusy = false;
                UpdateUI(true);
                SyncConnectionState();
                RefreshSessionStatus();
            }
        }

        /// <summary>
        /// Background rescans stay silent so the list doesn't flicker every five
        /// seconds; only an explicit scan reports progress.
        /// </summary>
        private async Task DiscoverAndDisplayDevicesAsync(bool showProgress = false)
        {
            if (_isScanning)
            {
                return;
            }

            _isScanning = true;
            var announce = showProgress || _allDevices.Count == 0;
            if (announce)
            {
                searchButton.IsEnabled = false;
                deviceCountText.Text = "Looking for devices…";
            }

            var cts = new CancellationTokenSource();

            try
            {
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync(cts.Token);
                MergeDiscoveredDevices(discoveredDevices);
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
                _isScanning = false;
                searchButton.IsEnabled = true;
            }
        }

        private async Task TryAutoConnectToLastReceiverAsync()
        {
            var settings = _captureMonitor.Settings;
            if (_autoConnectAttempted ||
                !settings.AutoConnectLastReceiver ||
                string.IsNullOrWhiteSpace(settings.LastReceiverKey) ||
                _streamingOrchestrator.State != SessionState.Disconnected)
            {
                return;
            }

            var device = _allDevices.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Key,
                    settings.LastReceiverKey,
                    StringComparison.Ordinal));
            if (device is null || device.IsBusy)
            {
                return;
            }

            _autoConnectAttempted = true;
            AppLog.Info("ui", "Startup receiver found; connecting automatically.");
            await SetDeviceConnectionAsync(device, connect: true, isAutomatic: true);
        }

        /// <summary>
        /// Folds a discovery pass into the existing rows so UI state (busy, status,
        /// connection) survives the five-second rescan.
        /// </summary>
        private void MergeDiscoveredDevices(List<DeviceInfo> discoveredDevices)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var discovered in discoveredDevices)
            {
                var key = DeviceViewModel.BuildKey(discovered);
                seen.Add(key);

                var existing = _allDevices.FirstOrDefault(d =>
                    string.Equals(d.Key, key, StringComparison.Ordinal));
                if (existing is null)
                {
                    _allDevices.Add(new DeviceViewModel(discovered));
                }
                else
                {
                    existing.Update(discovered);
                }
            }

            // A device that misses one scan pass but is still streaming stays listed.
            _allDevices.RemoveAll(d => !seen.Contains(d.Key) && !d.IsConnected);
            _allDevices.Sort((left, right) =>
                string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        }

        private void RebuildVisibleDevices()
        {
            var target = string.IsNullOrWhiteSpace(_filterText)
                ? _allDevices.ToList()
                : _allDevices.Where(d => d.MatchesFilter(_filterText)).ToList();

            for (var i = DeviceList.Count - 1; i >= 0; i--)
            {
                if (!target.Contains(DeviceList[i]))
                {
                    DeviceList.RemoveAt(i);
                }
            }

            for (var i = 0; i < target.Count; i++)
            {
                if (i >= DeviceList.Count)
                {
                    DeviceList.Add(target[i]);
                    continue;
                }

                if (!ReferenceEquals(DeviceList[i], target[i]))
                {
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
            if (_captureMonitor.Settings.AirPlayReceiverHintDismissed)
            {
                macHintBar.IsOpen = false;
                return;
            }

            macHintBar.IsOpen = _allDevices.Count > 0;
        }

        private void MacHintBar_CloseButtonClick(InfoBar sender, object args)
        {
            _captureMonitor.DismissAirPlayReceiverHint();
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
                device.IsConnected = connected.Any(receiver => ReceiverEquals(receiver, device.Device));
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

        private string CreateDeviceSummary(DeviceInfo device)
        {
            var protocol = StreamingOrchestrator.DescribePreferredProtocol(device);

            return $"Model: {Fallback(device.Model)}\n" +
                   $"Manufacturer: {Fallback(device.Manufacturer)}\n" +
                   $"Address: {Fallback(device.IPAddress)}:{device.Port}\n" +
                   $"Protocol: {protocol}\n" +
                   $"Encryption: {Fallback(device.EncryptionTypes)}";
        }

        private static string Fallback(string value) =>
            string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

        private static bool ReceiverEquals(DeviceInfo left, DeviceInfo right)
        {
            if (!string.IsNullOrWhiteSpace(left.DeviceID) &&
                !string.IsNullOrWhiteSpace(right.DeviceID))
            {
                return string.Equals(left.DeviceID, right.DeviceID, StringComparison.Ordinal);
            }

            return string.Equals(left.IPAddress, right.IPAddress, StringComparison.Ordinal) &&
                   left.Port == right.Port;
        }

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
