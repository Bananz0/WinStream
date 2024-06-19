using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Zeroconf;
using System.Windows.Data;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<DeviceInfo> DeviceList { get; } = new ObservableCollection<DeviceInfo>();
        // Test list for debugging
        public ObservableCollection<DeviceInfo> TestList { get; } = new ObservableCollection<DeviceInfo>
        {
            new DeviceInfo { DisplayName = "Test Device 1", IPAddress = "192.168.1.1", ToolTipText = "Test Tooltip 1" },
            new DeviceInfo { DisplayName = "Test Device 2", IPAddress = "192.168.1.2", ToolTipText = "Test Tooltip 2" }
        };

        private CollectionViewSource deviceListView;

        public MainWindow()
        {
            InitializeComponent();
            deviceListView = new CollectionViewSource { Source = DeviceList };
            devicesList.ItemsSource = deviceListView.View;
            Debug.WriteLine("Application started, UI initialized.");
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Search button clicked.");
            await DiscoverAirPlayDevices();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("Refresh button clicked.");
            await DiscoverAirPlayDevices();  // Assuming the same function should be used for refresh
        }

        private async void DevicesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is DeviceInfo deviceInfo)
            {
                Debug.WriteLine($"Device selected: {deviceInfo.DisplayName} at {deviceInfo.IPAddress}");

                // Display a dialog with more information about the selected device
                ContentDialog deviceDetailsDialog = new ContentDialog
                {
                    Title = $"Device Details: {deviceInfo.DisplayName}",
                    Content = $"IP Address: {deviceInfo.IPAddress}\nTooltip Text: {deviceInfo.ToolTipText}",
                    CloseButtonText = "OK"
                };

                // Show the dialog and get the result
                ContentDialogResult result = await deviceDetailsDialog.ShowAsync();

                // Handle the result if needed
                if (result == ContentDialogResult.Primary)
                {
                    // User clicked OK
                    Debug.WriteLine("User clicked OK");
                }
                else
                {
                    // User clicked Cancel or closed the dialog
                    Debug.WriteLine("User canceled or closed the dialog");
                }
            }
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            string filterText = filterTextBox.Text.ToLower();
            deviceListView.View.Filter = new Predicate<object>(item =>
            {
                DeviceInfo device = item as DeviceInfo;
                return device.DisplayName.ToLower().Contains(filterText) || device.IPAddress.ToLower().Contains(filterText);
            });
            deviceListView.View.Refresh();

            Debug.WriteLine("Filter applied.");
        }


        // Now properly named and focused on discovering AirPlay devices
        private async Task DiscoverAirPlayDevices()
        {
            DeviceList.Clear();
            UpdateUI("Searching for AirPlay Devices...", false);
            progressBar.Visibility = Visibility.Visible;

            try
            {
                var results = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", scanTime: TimeSpan.FromSeconds(5));
                Debug.WriteLine($"Discovery complete. Found {results.Count()} devices.");

                if (!results.Any())
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateUI("No devices found. Try again?", true);
                        Debug.WriteLine("No AirPlay devices found.");
                    });
                }
                else
                {
                    foreach (var host in results)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            DeviceList.Add(new DeviceInfo
                            {
                                DisplayName = host.DisplayName,
                                IPAddress = host.IPAddress,
                                ToolTipText = $"IP Address: {host.IPAddress}"
                            });
                            Debug.WriteLine($"Found: {host.DisplayName} at {host.IPAddress}");
                        });
                    }
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateUI("AirPlay Devices Discovered - " + DeviceList.Count + " device(s) found", true);
                    });
                }
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateUI("Error during discovery: " + ex.Message, true);
                    Debug.WriteLine("Error during device discovery: " + ex.Message);
                    LogException(ex);
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

        private void UpdateUI(string content, bool isEnabled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                searchButton.Content = content;
                searchButton.IsEnabled = isEnabled;
                refreshButton.IsEnabled = isEnabled;  // Ensure this button is also updated
                Debug.WriteLine($"UI updated: Button content - {content}, Enabled - {isEnabled}");
            });
        }

        private void LogException(Exception ex)
        {
            string logFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStream", "Logs", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            using (StreamWriter writer = new StreamWriter(logFilePath, true))
            {
                writer.WriteLine($"{DateTime.Now}: {ex}");
            }
            Debug.WriteLine($"Exception logged to file: {logFilePath}");
        }
    }

    public class DeviceInfo
    {
        public string DisplayName { get; set; }
        public string IPAddress { get; set; }
        public string ToolTipText { get; set; }
    }
}
