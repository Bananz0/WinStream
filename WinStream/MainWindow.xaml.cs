using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Audio;
using WinStream.Core.Audio;
using WinStream.Core.Logging;
using WinStream.Core.Persistence;
using WinStream.Core.Streaming;
using WinStream.Network;
using WinStream.Streaming;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DeviceInfo> DeviceList { get; } = new ObservableCollection<DeviceInfo>();
        private readonly CaptureMonitorService _captureMonitor = new();
        private readonly StreamingOrchestrator _streamingOrchestrator = new();
        private readonly DispatcherTimer _scanTimer;
        private readonly DispatcherTimer _captureLevelTimer;
        private readonly AppWindow _appWindow;
        private bool _allowClose;
        private bool _suppressCaptureSelectionEvents;

        public MainWindow()
        {
            InitializeComponent();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.Resize(new Windows.Graphics.SizeInt32(760, 620));
            _appWindow.Closing += OnAppWindowClosing;

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _scanTimer.Tick += async (_, _) => await DiscoverAndDisplayDevicesAsync();
            _scanTimer.Start();

            _captureLevelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _captureLevelTimer.Tick += (_, _) => UpdateCaptureLevelUi();
            _captureLevelTimer.Start();

            _captureMonitor.StateChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(RefreshCaptureStatus);
            _streamingOrchestrator.StateChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(() => UpdateUI(true));

            LoadCaptureEndpoints();
            RestoreCaptureSettings();
            airPlay2GateToggle.IsOn = _captureMonitor.Settings.EnableAirPlay2Experimental;
            _streamingOrchestrator.EnableAirPlay2Experimental =
                _captureMonitor.Settings.EnableAirPlay2Experimental;
            captureModeComboBox.SelectedIndex =
                _captureMonitor.Settings.CaptureMode == CaptureMode.VirtualDriver ? 1 : 0;
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
            await DiscoverAndDisplayDevicesAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCaptureEndpoints();
            await DiscoverAndDisplayDevicesAsync();
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(filterTextBox.Text.ToLowerInvariant());
        }

        private void ApplyFilter(string filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                devicesList.ItemsSource = DeviceList;
            }
            else
            {
                devicesList.ItemsSource = DeviceList.Where(d =>
                    (d.DisplayName?.ToLowerInvariant().Contains(filterText) ?? false) ||
                    (d.IPAddress?.ToLowerInvariant().Contains(filterText) ?? false));
            }
        }

        private async void CaptureDeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCaptureSelectionEvents)
            {
                return;
            }

            if (captureDeviceComboBox.SelectedItem is RenderEndpointInfo endpoint)
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
                var endpoints = _captureMonitor.ListEndpoints();
                captureDeviceComboBox.ItemsSource = endpoints;

                var selectedId = _captureMonitor.Settings.SelectedRenderDeviceId;
                var selected = endpoints.FirstOrDefault(e =>
                                   string.Equals(e.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                               ?? endpoints.FirstOrDefault(e => e.IsDefault)
                               ?? endpoints.FirstOrDefault();
                captureDeviceComboBox.SelectedItem = selected;
            }
            catch (Exception ex)
            {
                AppLog.Warn("capture", $"Failed to enumerate capture endpoints: {ex.GetType().Name}");
                captureStatusText.Text = "No audio devices";
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
                captureStatusText.Foreground = new SolidColorBrush(Colors.Gray);
                return;
            }

            if (_captureMonitor.IsSilent)
            {
                captureStatusText.Text = "Silent";
                captureStatusText.Foreground = new SolidColorBrush(Colors.Orange);
                return;
            }

            var format = _captureMonitor.Format;
            captureStatusText.Text = format is null ? "Capturing" : format.ToString();
            captureStatusText.Foreground = new SolidColorBrush(Colors.SeaGreen);
        }

        private void AirPlay2GateToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var enabled = airPlay2GateToggle.IsOn;
            _streamingOrchestrator.EnableAirPlay2Experimental = enabled;
            _captureMonitor.SetAirPlay2Experimental(enabled);
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
                    captureStatusText.Text = "Driver mode: install optional VAD (not in Store MSIX).";
                    captureStatusText.Foreground = new SolidColorBrush(Colors.DarkOrange);
                }
            }
        }

        private async void StreamVolumeSlider_ValueChanged(
            object sender,
            RangeBaseValueChangedEventArgs e)
        {
            if (streamVolumeText is not null)
            {
                streamVolumeText.Text = $"{e.NewValue:0} dB";
            }

            if (_streamingOrchestrator.State == SessionState.Streaming)
            {
                await _streamingOrchestrator.SetVolumeAsync((float)e.NewValue);
            }
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DeviceInfo deviceInfo)
            {
                var container = (button.Parent as FrameworkElement)?.Parent as Grid;
                if (container != null)
                {
                    var progressRing = container.FindName("connectProgressRing") as ProgressRing;
                    var statusTextBlock = container.FindName("connectStatusTextBlock") as TextBlock;

                    AppLog.Info("ui", "Connecting to selected receiver.");
                    UpdateUI(false);
                    progressRing.Visibility = Visibility.Visible;
                    statusTextBlock.Text = string.Empty;

                    try
                    {
                        if (_streamingOrchestrator.ConnectedReceivers.Any(receiver =>
                                ReceiverEquals(receiver, deviceInfo)))
                        {
                            await _streamingOrchestrator.DisconnectAsync(deviceInfo);
                            statusTextBlock.Text = _streamingOrchestrator.ConnectedReceivers.Count == 0
                                ? "Disconnected."
                                : $"Removed. {_streamingOrchestrator.State} ({_streamingOrchestrator.ConnectedReceivers.Count} left).";
                            statusTextBlock.Foreground = new SolidColorBrush(Colors.Gray);
                            button.Content = "Connect";
                            return;
                        }

                        await _captureMonitor.EnsureStartedAsync();
                        var source = _captureMonitor.GetSourceForStreaming()
                            ?? throw new InvalidOperationException(
                                "Capture source is not available.");
                        await _streamingOrchestrator.ConnectAsync(deviceInfo, source);
                        await _streamingOrchestrator.SetVolumeAsync(
                            (float)streamVolumeSlider.Value);
                        statusTextBlock.Text =
                            $"{_streamingOrchestrator.State} ({_streamingOrchestrator.ConnectedReceivers.Count} room(s)).";
                        statusTextBlock.Foreground = new SolidColorBrush(
                            _streamingOrchestrator.State == SessionState.Degraded
                                ? Colors.Orange
                                : Colors.Green);
                        button.Content = "Disconnect";
                    }
                    catch (Exception ex)
                    {
                        statusTextBlock.Text =
                            $"Connection failed: {FormatConnectionFailure(ex.Message)}";
                        statusTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                        AppLog.Error("ui", $"Connection error: {ex.GetType().Name}");
                    }
                    finally
                    {
                        progressRing.Visibility = Visibility.Collapsed;
                        UpdateUI(true);
                    }
                }
            }
        }

        private async Task DiscoverAndDisplayDevicesAsync()
        {
            UpdateUI(false);
            progressBar.Visibility = Visibility.Visible;
            var cts = new CancellationTokenSource();

            try
            {
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync(cts.Token);
                UpdateDeviceList(discoveredDevices);
                searchButton.Content = $"Devices Updated ({DeviceList.Count})";
            }
            catch (Exception ex)
            {
                AppLog.Error("ui", $"Discovery error: {ex.GetType().Name}");
                searchButton.Content = "Discovery Error";
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
                UpdateUI(true);
            }
        }

        private void ExpandToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggleButton)
            {
                var parentGrid = toggleButton.Parent as Grid;
                if (parentGrid != null)
                {
                    var expandedInfo = parentGrid.FindName("ExpandedInfo") as StackPanel;
                    if (expandedInfo != null)
                    {
                        expandedInfo.Visibility = toggleButton.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
            }
        }

        private void UpdateDeviceList(List<DeviceInfo> discoveredDevices)
        {
            var currentDevices = new HashSet<string>(discoveredDevices.Select(d => d.IPAddress));

            foreach (var device in DeviceList.ToList())
            {
                if (!currentDevices.Contains(device.IPAddress))
                {
                    DeviceList.Remove(device);
                }
            }

            foreach (var discoveredDevice in discoveredDevices)
            {
                var existingDevice = DeviceList.FirstOrDefault(d => d.IPAddress == discoveredDevice.IPAddress);
                if (existingDevice != null)
                {
                    existingDevice.DisplayName = discoveredDevice.DisplayName;
                    existingDevice.Manufacturer = discoveredDevice.Manufacturer;
                    existingDevice.Model = discoveredDevice.Model;
                    existingDevice.IPAddress = discoveredDevice.IPAddress;
                    existingDevice.ToolTipText = CreateTooltipSummary(existingDevice);
                }
                else
                {
                    discoveredDevice.ToolTipText = CreateTooltipSummary(discoveredDevice);
                    DeviceList.Add(discoveredDevice);
                }
            }
        }

        private string CreateTooltipSummary(DeviceInfo device)
        {
            var classic = AirPlayCapability.SupportsClassicRaop(device.EncryptionTypes);
            var ap2 = AirPlayCapability.SupportsAirPlay2(
                !string.IsNullOrWhiteSpace(device.PublicCUAirPlayPairingIdentity),
                device.Features,
                device.AirPlayVersion);
            var protocol = classic
                ? "Classic RAOP"
                : ap2
                    ? "AirPlay 2 (gated)"
                    : "Unknown";
            var et = string.IsNullOrWhiteSpace(device.EncryptionTypes)
                ? "n/a"
                : device.EncryptionTypes;
            return $"Name: {device.DisplayName}\n" +
                   $"Model: {device.Model}\n" +
                   $"IP: {device.IPAddress}\n" +
                   $"Port: {device.Port}\n" +
                   $"Encryption: {et}\n" +
                   $"Protocol: {protocol}";
        }

        private async void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DeviceInfo deviceInfo)
            {
                var dialog = new ContentDialog()
                {
                    Title = deviceInfo.DisplayName,
                    Content = new ScrollViewer
                    {
                        Content = new TextBlock
                        {
                            Text = deviceInfo.ToolTipText,
                            TextWrapping = TextWrapping.Wrap
                        },
                        VerticalScrollMode = ScrollMode.Auto,
                        HorizontalScrollMode = ScrollMode.Disabled
                    },
                    CloseButtonText = "Close"
                };

                dialog.XamlRoot = this.Content.XamlRoot;
                await dialog.ShowAsync();
            }
        }

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
                    " On a Mac, set AirPlay Receiver to allow Everyone " +
                    "(or anyone on the same network) and disable a required password.";
            }

            return message;
        }

        private void UpdateUI(bool isEnabled)
        {
            searchButton.IsEnabled = isEnabled;
            refreshButton.IsEnabled = isEnabled;
        }
    }
}
