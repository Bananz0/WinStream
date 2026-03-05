using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using WinStream.Network;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DeviceInfo> DeviceList { get; } = new();
        private DispatcherTimer _scanTimer;
        private AirPlayConnectionResult _currentConnection;

        public MainWindow()
        {
            InitializeComponent();

            // Mica material (Windows 11+)
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop();

            // Initial window size
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(540, 680));

            InitializeScanTimer();
            _ = DiscoverAndDisplayDevicesAsync();
        }

        private void InitializeScanTimer()
        {
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _scanTimer.Tick += async (_, _) => await DiscoverAndDisplayDevicesAsync();
            _scanTimer.Start();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
            => await DiscoverAndDisplayDevicesAsync();

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyFilter(filterTextBox.Text.ToLowerInvariant());

        private void ApplyFilter(string filterText)
        {
            devicesRepeater.ItemsSource = string.IsNullOrWhiteSpace(filterText)
                ? DeviceList
                : (System.Collections.IEnumerable)DeviceList.Where(d =>
                    d.DisplayName.ToLowerInvariant().Contains(filterText) ||
                    d.IPAddress.ToLowerInvariant().Contains(filterText));
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: DeviceInfo deviceInfo })
                return;

            // Disconnect if already streaming
            if (deviceInfo.IsStreaming)
            {
                deviceInfo.StatusText = "Disconnecting...";
                await DeviceConnection.StopStreamingAsync();
                deviceInfo.IsStreaming = false;
                deviceInfo.StatusText = string.Empty;
                _currentConnection = null;
                UpdateStreamingStatus(null);
                return;
            }

            UpdateUI(false);
            deviceInfo.IsConnecting = true;
            deviceInfo.StatusText = "Connecting...";

            try
            {
                // Stop any existing session first
                if (_currentConnection?.AudioSession != null)
                {
                    await DeviceConnection.StopStreamingAsync();
                    foreach (var d in DeviceList)
                    {
                        d.IsStreaming = false;
                        d.StatusText = string.Empty;
                    }
                }

                var result = await DeviceConnection.ConnectAndStreamAsync(deviceInfo, startStreaming: true);
                _currentConnection = result;

                if (result.Success)
                {
                    if (result.AudioSession?.IsStreaming == true)
                    {
                        deviceInfo.IsStreaming = true;
                        deviceInfo.StatusText = string.Empty;
                        UpdateStreamingStatus(result.AudioSession.CaptureDeviceName);
                    }
                    else
                    {
                        deviceInfo.StatusText = "Connected";
                    }
                }
                else
                {
                    deviceInfo.StatusText = "Failed";
                    Logger.LogMessage(result.Message, "connection");
                }
            }
            catch (Exception ex)
            {
                deviceInfo.StatusText = "Error";
                Logger.LogException(ex);
            }
            finally
            {
                deviceInfo.IsConnecting = false;
                UpdateUI(true);
            }
        }

        private async Task DiscoverAndDisplayDevicesAsync()
        {
            UpdateUI(false);
            scanProgressRing.IsActive = true;

            try
            {
                using var cts = new CancellationTokenSource();
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync(cts.Token);
                UpdateDeviceList(discoveredDevices);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Discovery error: {ex.Message}");
            }
            finally
            {
                scanProgressRing.IsActive = false;
                UpdateUI(true);
            }
        }

        private void UpdateDeviceList(List<DeviceInfo> discoveredDevices)
        {
            var currentAddresses = new System.Collections.Generic.HashSet<string>(
                discoveredDevices.Select(d => d.IPAddress));

            foreach (var device in DeviceList.ToList())
            {
                if (!currentAddresses.Contains(device.IPAddress))
                    DeviceList.Remove(device);
            }

            foreach (var discovered in discoveredDevices)
            {
                var existing = DeviceList.FirstOrDefault(d => d.IPAddress == discovered.IPAddress);
                if (existing != null)
                {
                    existing.DisplayName = discovered.DisplayName;
                    existing.Manufacturer = discovered.Manufacturer;
                    existing.Model = discovered.Model;
                }
                else
                {
                    DeviceList.Add(discovered);
                }
            }
        }

        private void UpdateStreamingStatus(string captureDeviceName)
        {
            if (captureDeviceName != null)
            {
                audioSourceTextBlock.Text = $"Audio source: {captureDeviceName}";
                streamingDot.Visibility = Visibility.Visible;
                streamingLabel.Visibility = Visibility.Visible;
            }
            else
            {
                audioSourceTextBlock.Text = "Audio source: not streaming";
                streamingDot.Visibility = Visibility.Collapsed;
                streamingLabel.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateUI(bool isEnabled)
        {
            refreshButton.IsEnabled = isEnabled;
        }
    }
}
