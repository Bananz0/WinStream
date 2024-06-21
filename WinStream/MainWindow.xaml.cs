using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using WinStream.Network;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DeviceInfo> DeviceList { get; } = new ObservableCollection<DeviceInfo>();
        private DispatcherTimer scanTimer;

        public MainWindow()
        {
            InitializeComponent();
            Debug.WriteLine("Application started, UI initialized.");

            InitializeTimer();
            _ = DiscoverAndDisplayDevicesAsync();
        }

        private void InitializeTimer()
        {
            scanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            scanTimer.Tick += async (s, e) => await DiscoverAndDisplayDevicesAsync();
            scanTimer.Start();
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Search button clicked.");
            await DiscoverAndDisplayDevicesAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Refresh button clicked.");
            await DiscoverAndDisplayDevicesAsync();
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(filterTextBox.Text.ToLower());
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
                    d.DisplayName.ToLower().Contains(filterText) ||
                    d.IPAddress.ToLower().Contains(filterText));
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

                    Debug.WriteLine($"Connecting to {deviceInfo.DisplayName} at {deviceInfo.IPAddress}:{deviceInfo.Port}");
                    UpdateUI(false);
                    progressRing.Visibility = Visibility.Visible;
                    statusTextBlock.Text = string.Empty;

                    try
                    {
                        using var rsaPublicKey = RSA.Create();
                        await DeviceConnection.ConnectToAirPlayServer(deviceInfo.IPAddress, deviceInfo.Port, rsaPublicKey);
                        statusTextBlock.Text = "Connected successfully.";
                        statusTextBlock.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Green);
                    }
                    catch (Exception ex)
                    {
                        statusTextBlock.Text = "Connection failed.";
                        statusTextBlock.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red);
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

            try
            {
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync();
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
                    existingDevice.ToolTipText = discoveredDevice.ToolTipText;
                }
                else
                {
                    DeviceList.Add(discoveredDevice);
                }
            }
        }

        private void UpdateUI(bool isEnabled)
        {
            searchButton.IsEnabled = isEnabled;
            refreshButton.IsEnabled = isEnabled;
        }
    }
}
