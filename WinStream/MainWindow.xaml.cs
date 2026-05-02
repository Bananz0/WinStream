using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using WinStream.Network;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        private const int MinAirPlayPinLength = 4;
        private const int MaxAirPlayPinLength = 8;

        public ObservableCollection<DeviceInfo> DeviceList { get; } = new();
        private DispatcherTimer _scanTimer;
        private AirPlayConnectionResult _currentConnection;
        private readonly SemaphoreSlim _scanLock = new(1, 1);
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private volatile bool _connectInProgress;
        private int _lastDiscoveryCount = -1;
        private int _discoveryScanCount;

        public MainWindow()
        {
            InitializeComponent();
            var exePath = Environment.ProcessPath ?? "unknown";
            var exeTimestamp = exePath != "unknown" ? File.GetLastWriteTimeUtc(exePath) : DateTime.MinValue;
            Logger.LogMessage(
                $"WinStream startup build={typeof(MainWindow).Assembly.GetName().Version} exe={exePath} exeUtc={exeTimestamp:O} utc={DateTime.UtcNow:O}",
                "startup");

            // Mica material (Windows 11+)
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop();

            // Initial window size
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(540, 680));

            UpdateDeviceList(DeviceDiscovery.GetKnownDevicesSnapshot());
            InitializeScanTimer();
            _ = DiscoverAndDisplayDevicesAsync();
        }

        private void InitializeScanTimer()
        {
            _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
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
            if (sender is not Button { Tag: DeviceInfo deviceInfo })
                return;

            if (!await _connectLock.WaitAsync(0))
            {
                Logger.LogMessage("Connect request ignored: another connect/disconnect operation is already in progress.", "connection");
                return;
            }

            // Disconnect if already streaming
            var resumeScanTimer = false;
            try
            {
                _connectInProgress = true;
                resumeScanTimer = _scanTimer?.IsEnabled == true;
                if (resumeScanTimer)
                {
                    _scanTimer.Stop();
                }

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

                AirPlay2AuthService.ClearRuntimePin();
                var result = await DeviceConnection.ConnectAndStreamAsync(deviceInfo, startStreaming: true);
                var pinPromptAttempts = 0;
                while (ShouldPromptForAirPlayPin(deviceInfo, result) && pinPromptAttempts < 2)
                {
                    var pin = await PromptForAirPlayPinAsync(deviceInfo);
                    if (!string.IsNullOrWhiteSpace(pin))
                    {
                        AirPlay2AuthService.SetRuntimePin(pin);
                        deviceInfo.StatusText = "Pairing...";
                        result = await DeviceConnection.ConnectAndStreamAsync(
                            deviceInfo,
                            startStreaming: true,
                            preferAirPlay2Auth: true);
                        pinPromptAttempts++;
                    }
                    else
                    {
                        result.Message = "Pairing canceled: AirPlay PIN was not provided.";
                        break;
                    }
                }

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
                if (resumeScanTimer && _scanTimer != null && !_scanTimer.IsEnabled)
                {
                    _scanTimer.Start();
                }
                _connectInProgress = false;
                _connectLock.Release();
            }
        }

        private async Task DiscoverAndDisplayDevicesAsync()
        {
            if (_connectInProgress)
            {
                return;
            }

            if (!await _scanLock.WaitAsync(0))
            {
                return;
            }

            UpdateUI(false);
            scanProgressRing.IsActive = true;

            try
            {
                var sw = Stopwatch.StartNew();
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync(CancellationToken.None);
                UpdateDeviceList(discoveredDevices);
                _discoveryScanCount++;
                if (discoveredDevices.Count != _lastDiscoveryCount || _discoveryScanCount % 15 == 0)
                {
                    Logger.LogMessage(
                        $"Discovery completed in {sw.ElapsedMilliseconds}ms with {discoveredDevices.Count} device(s).",
                        "discovery");
                    _lastDiscoveryCount = discoveredDevices.Count;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Discovery error: {ex.Message}");
                Logger.LogMessage($"Discovery error: {ex.Message}", "discovery");
            }
            finally
            {
                scanProgressRing.IsActive = false;
                UpdateUI(true);
                _scanLock.Release();
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

        private static bool ShouldPromptForAirPlayPin(DeviceInfo deviceInfo, AirPlayConnectionResult result)
        {
            if (deviceInfo == null || result == null || result.Success || !deviceInfo.IsAirPlay2Device)
            {
                return false;
            }

            return result.RequiresPin;
        }

        private async Task<string> PromptForAirPlayPinAsync(DeviceInfo deviceInfo)
        {
            var pinBox = new PasswordBox
            {
                PlaceholderText = "AirPlay PIN",
                MaxLength = MaxAirPlayPinLength
            };

            var content = new StackPanel
            {
                Spacing = 8
            };
            content.Children.Add(new TextBlock
            {
                Text = $"Enter the PIN shown on {deviceInfo.DisplayName}."
            });
            content.Children.Add(pinBox);

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "AirPlay Pairing PIN",
                Content = content,
                PrimaryButtonText = "Pair",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var response = await dialog.ShowAsync();
            if (response != ContentDialogResult.Primary)
            {
                return string.Empty;
            }

            var pin = (pinBox.Password ?? string.Empty).Trim();
            var isValid = pin.Length >= MinAirPlayPinLength &&
                          pin.Length <= MaxAirPlayPinLength &&
                          pin.All(char.IsDigit);
            if (!isValid)
            {
                Logger.LogMessage("Invalid AirPlay PIN entered. Expected 4-8 numeric digits.", "connection");
                return string.Empty;
            }

            return pin;
        }
    }
}
