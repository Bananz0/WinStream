using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        private string _currentFilterText = string.Empty;
        private DateTimeOffset? _lastScanCompletedAt;
        private string _statusOverrideText;

        public MainWindow()
        {
            InitializeComponent();

            var exePath = Environment.ProcessPath ?? "unknown";
            var exeTimestamp = exePath != "unknown" ? File.GetLastWriteTimeUtc(exePath) : DateTime.MinValue;
            Logger.LogMessage(
                $"WinStream startup build={typeof(MainWindow).Assembly.GetName().Version} exe={exePath} exeUtc={exeTimestamp:O} utc={DateTime.UtcNow:O}",
                "startup");

            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                SystemBackdrop = new MicaBackdrop();
            }

            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                ExtendsContentIntoTitleBar = true;
                SetTitleBar(AppTitleBar);
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(1240, 820));

            aboutVersionTextBlock.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
            aboutFrameworkTextBlock.Text = RuntimeInformation.FrameworkDescription;
            verboseRtspCheckBox.IsChecked = IsVerboseRtspLoggingEnabled();
            pinStatusTextBlock.Text = HasConfiguredPin()
                ? "A PIN is configured for the next AirPlay 2 pairing attempt."
                : "No PIN is configured.";

            navView.SelectedItem = devicesNavItem;
            UpdateDeviceList(DeviceDiscovery.GetKnownDevicesSnapshot());
            UpdateStreamingStatus(null);
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
            => ApplyFilter(filterTextBox.Text);

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                DevicesPanel.Visibility = Visibility.Collapsed;
                SettingsPanel.Visibility = Visibility.Visible;
                return;
            }

            if (args.SelectedItemContainer?.Tag is not string section)
            {
                return;
            }

            DevicesPanel.Visibility = section == "devices" ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void ApplyFilter(string filterText)
        {
            _currentFilterText = (filterText ?? string.Empty).Trim();
            UpdateDeviceRepeaterSource();
            UpdateDashboard();
        }

        private IReadOnlyList<DeviceInfo> GetFilteredDevices()
        {
            if (string.IsNullOrWhiteSpace(_currentFilterText))
            {
                return DeviceList.ToList();
            }

            var filter = _currentFilterText.ToLowerInvariant();
            return DeviceList
                .Where(d =>
                    (d.DisplayName?.ToLowerInvariant().Contains(filter) ?? false) ||
                    (d.IPAddress?.ToLowerInvariant().Contains(filter) ?? false) ||
                    (d.Model?.ToLowerInvariant().Contains(filter) ?? false) ||
                    (d.Manufacturer?.ToLowerInvariant().Contains(filter) ?? false))
                .ToList();
        }

        private void UpdateDeviceRepeaterSource()
        {
            devicesRepeater.ItemsSource = GetFilteredDevices();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: DeviceInfo deviceInfo })
            {
                return;
            }

            if (!await _connectLock.WaitAsync(0))
            {
                Logger.LogMessage("Connect request ignored: another connect/disconnect operation is already in progress.", "connection");
                return;
            }

            var resumeScanTimer = false;
            try
            {
                _connectInProgress = true;
                SetStatusOverride(deviceInfo.IsStreaming ? $"Disconnecting from {deviceInfo.DisplayName}" : $"Connecting to {deviceInfo.DisplayName}");
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
                    UpdateDashboard();
                    return;
                }

                UpdateUI(false);
                deviceInfo.IsConnecting = true;
                deviceInfo.StatusText = "Connecting...";
                UpdateDashboard();

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
                        UpdateDashboard();
                    }
                }
                else
                {
                    deviceInfo.StatusText = "Failed";
                    Logger.LogMessage(result.Message, "connection");
                    ShowConnectionInfo("Connection failed", result.Message, InfoBarSeverity.Error);
                    UpdateDashboard();
                }
            }
            catch (Exception ex)
            {
                deviceInfo.StatusText = "Error";
                Logger.LogException(ex);
                ShowConnectionInfo("Connection error", ex.Message, InfoBarSeverity.Error);
                UpdateDashboard();
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
                ClearStatusOverride();
                UpdateDashboard();
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
            SetStatusOverride("Scanning network");

            try
            {
                var sw = Stopwatch.StartNew();
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync(CancellationToken.None);
                _lastScanCompletedAt = DateTimeOffset.Now;
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
                ClearStatusOverride();
                UpdateDashboard();
                _scanLock.Release();
            }
        }

        private void UpdateDeviceList(List<DeviceInfo> discoveredDevices)
        {
            var currentAddresses = new HashSet<string>(discoveredDevices.Select(d => d.IPAddress));

            foreach (var device in DeviceList.ToList())
            {
                if (!currentAddresses.Contains(device.IPAddress))
                {
                    DeviceList.Remove(device);
                }
            }

            foreach (var discovered in discoveredDevices)
            {
                var existing = DeviceList.FirstOrDefault(d => d.IPAddress == discovered.IPAddress);
                if (existing != null)
                {
                    existing.DisplayName = discovered.DisplayName;
                    existing.Manufacturer = discovered.Manufacturer;
                    existing.Model = discovered.Model;
                    existing.PublicKey = discovered.PublicKey;
                    existing.RsaPublicKey = discovered.RsaPublicKey;
                    existing.SupportedCodecs = discovered.SupportedCodecs;
                    existing.EncryptionTypes = discovered.EncryptionTypes;
                    existing.SystemFlags = discovered.SystemFlags;
                    existing.RawTxtRecords = discovered.RawTxtRecords;
                    existing.Port = discovered.Port;
                }
                else
                {
                    DeviceList.Add(discovered);
                }
            }

            UpdateDeviceRepeaterSource();
            UpdateDashboard();
        }

        private void UpdateStreamingStatus(string captureDeviceName)
        {
            if (captureDeviceName != null)
            {
                audioSourceTextBlock.Text = captureDeviceName;
                streamingDot.Visibility = Visibility.Visible;
                streamingLabel.Visibility = Visibility.Visible;
                ShowConnectionInfo("Streaming", $"Audio source: {captureDeviceName}", InfoBarSeverity.Success);
            }
            else
            {
                audioSourceTextBlock.Text = "Not streaming";
                streamingDot.Visibility = Visibility.Collapsed;
                streamingLabel.Visibility = Visibility.Collapsed;
            }

            UpdateDashboard();
        }

        private void UpdateDashboard()
        {
            var filteredDevices = GetFilteredDevices();
            var streamingDevice = DeviceList.FirstOrDefault(d => d.IsStreaming);

            deviceCountValueTextBlock.Text = DeviceList.Count.ToString();
            scanStateValueTextBlock.Text = scanProgressRing.IsActive ? "Scanning" : _connectInProgress ? "Working" : "Idle";
            streamingStateValueTextBlock.Text = streamingDevice != null ? "Live" : "Off";
            lastScanValueTextBlock.Text = _lastScanCompletedAt?.ToString("HH:mm:ss") ?? "Waiting";

            var matchesLabel = filteredDevices.Count == 1 ? "receiver" : "receivers";
            deviceListHeaderTextBlock.Text = string.IsNullOrWhiteSpace(_currentFilterText)
                ? $"{filteredDevices.Count} {matchesLabel} available"
                : $"{filteredDevices.Count} {matchesLabel} match \"{_currentFilterText}\"";

            var hasDevices = filteredDevices.Count > 0;
            devicesRepeater.Visibility = hasDevices ? Visibility.Visible : Visibility.Collapsed;
            emptyStateCard.Visibility = hasDevices ? Visibility.Collapsed : Visibility.Visible;
            emptyStateTitleTextBlock.Text = DeviceList.Count == 0 ? "No AirPlay receivers found" : "No devices match the current search";
            emptyStateBodyTextBlock.Text = DeviceList.Count == 0
                ? "Run another discovery pass and confirm your receiver is on the same network."
                : "Try a different device name, model, manufacturer, or IP address.";

            titleBarStatusTextBlock.Text = !string.IsNullOrWhiteSpace(_statusOverrideText)
                ? _statusOverrideText
                : streamingDevice != null
                    ? $"Streaming to {streamingDevice.DisplayName}"
                    : DeviceList.Count == 0
                        ? "Ready to scan"
                        : $"{DeviceList.Count} device{(DeviceList.Count == 1 ? string.Empty : "s")} ready";
        }

        private void UpdateUI(bool isEnabled)
        {
            refreshButton.IsEnabled = isEnabled;
        }

        private void SetStatusOverride(string status)
        {
            _statusOverrideText = status;
            UpdateDashboard();
        }

        private void ClearStatusOverride()
        {
            _statusOverrideText = null;
            UpdateDashboard();
        }

        private void ApplyPinButton_Click(object sender, RoutedEventArgs e)
        {
            var pin = (airPlayPinPasswordBox.Password ?? string.Empty).Trim();
            if (!IsValidAirPlayPin(pin))
            {
                pinStatusTextBlock.Text = "PIN must be 4-8 digits.";
                ShowConnectionInfo("Invalid PIN", "AirPlay PINs must contain 4-8 digits.", InfoBarSeverity.Warning);
                return;
            }

            AirPlay2AuthService.SetRuntimePin(pin);
            pinStatusTextBlock.Text = "PIN configured for the next AirPlay 2 pairing attempt.";
            airPlayPinPasswordBox.Password = string.Empty;
            ShowConnectionInfo("PIN configured", "The next connection attempt will use the supplied AirPlay PIN.", InfoBarSeverity.Informational);
        }

        private void ClearPinButton_Click(object sender, RoutedEventArgs e)
        {
            AirPlay2AuthService.ClearRuntimePin();
            airPlayPinPasswordBox.Password = string.Empty;
            pinStatusTextBlock.Text = "No PIN is configured.";
            ShowConnectionInfo("PIN cleared", "Runtime AirPlay PIN has been cleared.", InfoBarSeverity.Informational);
        }

        private void ForgetPairingsButton_Click(object sender, RoutedEventArgs e)
        {
            AirPlay2AuthService.ForgetStoredCredentials();
            ShowConnectionInfo("Pairings cleared", "Stored AirPlay 2 pairing credentials were removed.", InfoBarSeverity.Informational);
        }

        private void VerboseRtspCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            Environment.SetEnvironmentVariable(
                RtspClient.VerboseLoggingEnvironmentVariable,
                verboseRtspCheckBox.IsChecked == true ? "1" : null);
        }

        private static bool HasConfiguredPin()
        {
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AirPlay2AuthService.PinEnvironmentVariable));
        }

        private static bool IsVerboseRtspLoggingEnabled()
        {
            var value = Environment.GetEnvironmentVariable(RtspClient.VerboseLoggingEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private void ShowConnectionInfo(string title, string message, InfoBarSeverity severity)
        {
            connectionInfoBar.Title = title;
            connectionInfoBar.Message = message ?? string.Empty;
            connectionInfoBar.Severity = severity;
            connectionInfoBar.IsOpen = true;
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
            while (true)
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
                if (IsValidAirPlayPin(pin))
                {
                    return pin;
                }

                Logger.LogMessage("Invalid AirPlay PIN entered. Expected 4-8 numeric digits.", "connection");
                ShowConnectionInfo("Invalid PIN", "AirPlay PINs must contain 4-8 digits.", InfoBarSeverity.Warning);
            }
        }

        private static bool IsValidAirPlayPin(string pin)
        {
            return pin.Length >= MinAirPlayPinLength &&
                   pin.Length <= MaxAirPlayPinLength &&
                   pin.All(char.IsDigit);
        }
    }
}
