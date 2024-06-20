using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using WinStream.Network;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DeviceInfo> DeviceList { get; } = new ObservableCollection<DeviceInfo>();
        private DispatcherTimer scanTimer;
        private CollectionViewSource deviceListView;

        public MainWindow()
        {
            InitializeComponent();
            deviceListView = new CollectionViewSource { Source = DeviceList };
            devicesList.ItemsSource = deviceListView.View;
            Debug.WriteLine("Application started, UI initialized.");

            InitializeTimer();
            _ = DiscoverAndDisplayDevicesAsync();
        }

        private void OnWindowClosing(object sender, WindowEventArgs e)
        {
            scanTimer?.Stop();
        }

        private void InitializeTimer()
        {
            scanTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
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

        private void DevicesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (devicesList.SelectedItem is DeviceInfo selectedDevice)
            {
                var container = devicesList.ContainerFromItem(selectedDevice) as ListViewItem;
                var expander = container?.ContentTemplateRoot as Expander;
                if (expander != null)
                {
                    expander.IsExpanded = true;
                }
            }
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyFilter(filterTextBox.Text.ToLower());
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter(filterTextBox.Text.ToLower());
        }

        private void ApplyFilter(string filterText)
        {
            deviceListView.View.Filter = string.IsNullOrWhiteSpace(filterText)
                ? (Predicate<object>)null
                : item => ((DeviceInfo)item)?.DisplayName?.ToLower().Contains(filterText) == true ||
                          ((DeviceInfo)item)?.IPAddress?.ToLower().Contains(filterText) == true;
            deviceListView.View.Refresh();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is DeviceInfo deviceInfo)
            {
                Debug.WriteLine($"Connecting to {deviceInfo.DisplayName} at {deviceInfo.IPAddress}:{deviceInfo.Port}");
                searchButton.IsEnabled = false;  // Disable the search button while connecting
                progressBar.Visibility = Visibility.Visible;  // Show progress bar

                await DeviceConnection.ConnectToAirPlayServer(deviceInfo.IPAddress, deviceInfo.Port);
                progressBar.Visibility = Visibility.Collapsed;  // Hide progress bar
                searchButton.IsEnabled = true;  // Re-enable the search button
            }
        }

        private async Task DiscoverAndDisplayDevicesAsync()
        {
            UpdateUI("Searching for AirPlay Devices...", false);
            progressBar.Visibility = Visibility.Visible;

            try
            {
                var discoveredDevices = await DeviceDiscovery.DiscoverDevicesAsync();
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateDeviceList(discoveredDevices);
                    UpdateUI($"AirPlay Devices Updated - {DeviceList.Count} device(s) found", true);
                });
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateUI($"Error during discovery: {ex.Message}", true);
                    Debug.WriteLine($"Error during device discovery: {ex.Message}");
                    Logger.LogException(ex);
                });
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    progressBar.Visibility = Visibility.Collapsed;
                });
            }
        }

        private void UpdateDeviceList(List<DeviceInfo> discoveredDevices)
        {
            var currentIPs = discoveredDevices.Select(d => d.IPAddress).ToList();
            var devicesToRemove = DeviceList.Where(d => !currentIPs.Contains(d.IPAddress)).ToList();
            foreach (var device in devicesToRemove)
            {
                DeviceList.Remove(device);
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

        private void UpdateUI(string content, bool isEnabled)
        {
            searchButton.Content = content;
            searchButton.IsEnabled = isEnabled;
            refreshButton.IsEnabled = isEnabled;
        }
    }
}

