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
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using WinStream.Audio;
using WinStream.Core.Audio;
using WinStream.Network;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DeviceInfo> DeviceList { get; } = new ObservableCollection<DeviceInfo>();
        private readonly CaptureMonitorService _captureMonitor = new();
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
            _appWindow.Resize(new Windows.Graphics.SizeInt32(760, 560));
            _appWindow.Closing += OnAppWindowClosing;

            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _scanTimer.Tick += async (_, _) => await DiscoverAndDisplayDevicesAsync();
            _scanTimer.Start();

            _captureLevelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _captureLevelTimer.Tick += (_, _) => UpdateCaptureLevelUi();
            _captureLevelTimer.Start();

            _captureMonitor.StateChanged += (_, _) =>
                DispatcherQueue.TryEnqueue(RefreshCaptureStatus);

            LoadCaptureEndpoints();
            RestoreCaptureSettings();
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

        public void CloseForExit()
        {
            _allowClose = true;
            _scanTimer.Stop();
            _captureLevelTimer.Stop();
            _ = _captureMonitor.DisposeAsync().AsTask();
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
                Debug.WriteLine($"Failed to enumerate capture endpoints: {ex.Message}");
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

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DeviceInfo deviceInfo)
            {
                var container = (button.Parent as FrameworkElement)?.Parent as Grid;
                if (container != null)
                {
                    var progressRing = container.FindName("connectProgressRing") as ProgressRing;
                    var statusTextBlock = container.FindName("connectStatusTextBlock") as TextBlock;

                    Debug.WriteLine($"Connecting to {deviceInfo.DisplayName} at {deviceInfo.IPAddress}:{deviceInfo.Port}");
                    UpdateUI(false);
                    progressRing.Visibility = Visibility.Visible;
                    statusTextBlock.Text = string.Empty;

                    try
                    {
                        using var rsaPublicKey = RSA.Create();
                        await DeviceConnection.ConnectToAirPlayServer(deviceInfo.IPAddress, deviceInfo.Port, rsaPublicKey);
                        statusTextBlock.Text = "Connected successfully.";
                        statusTextBlock.Foreground = new SolidColorBrush(Colors.Green);
                    }
                    catch (Exception ex)
                    {
                        statusTextBlock.Text = "Connection failed.";
                        statusTextBlock.Foreground = new SolidColorBrush(Colors.Red);
                        Debug.WriteLine($"Connection error: {ex.Message}");
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
                Debug.WriteLine($"Error during device discovery: {ex.Message}");
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
            return $"Device Name: {device.DeviceName}\n" +
                   $"IP Address: {device.IPAddress}\n" +
                   $"Port: {device.Port}\n" +
                   $"Manufacturer: {device.Manufacturer}\n" +
                   $"Model: {device.Model}\n" +
                   $"Firmware Version: {device.FirmwareVersion}\n" +
                   $"OS Version: {device.OSVersion}\n" +
                   $"Bluetooth Address: {device.BluetoothAddress}\n" +
                   $"Device ID: {device.DeviceID}\n" +
                   $"Protocol Version: {device.ProtocolVersion}\n" +
                   $"AirPlay Version: {device.AirPlayVersion}\n" +
                   $"Serial Number: {device.SerialNumber}\n" +
                   $"Public CU AirPlay Pairing Identity: {device.PublicCUAirPlayPairingIdentity}\n" +
                   $"Public CU System Pairing Identity: {device.PublicCUSystemPairingIdentity}\n" +
                   $"Public Key: {device.PublicKey}\n" +
                   $"Household ID: {device.HouseholdID}\n" +
                   $"Group UUID: {device.GroupUUID}\n" +
                   $"Is Group Leader: {device.IsGroupLeader}\n" +
                   $"Required Sender Features: {device.RequiredSenderFeatures}\n" +
                   $"System Flags: {device.SystemFlags}";
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

        private void UpdateUI(bool isEnabled)
        {
            searchButton.IsEnabled = isEnabled;
            refreshButton.IsEnabled = isEnabled;
        }
    }
}
