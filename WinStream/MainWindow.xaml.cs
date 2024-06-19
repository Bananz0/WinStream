using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using Zeroconf;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace WinStream
{
    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<IZeroconfHost> Devices { get; } = new ObservableCollection<IZeroconfHost>();

        public MainWindow()
        {
            InitializeComponent();
            devicesList.ItemsSource = Devices;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            await DiscoverDevicesAsync("Discover AirPlay Devices");
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await DiscoverDevicesAsync("Refreshed Discovery");
        }

        private async Task DiscoverDevicesAsync(string actionName)
        {
            Devices.Clear();
            UpdateUI("Searching...", false);
            progressBar.Visibility = Visibility.Visible;

            try
            {
                var scanDuration = TimeSpan.FromMilliseconds(60000);
                var results = await ZeroconfResolver.ResolveAsync("_airplay._tcp.local.", scanTime: scanDuration);
                Debug.WriteLine($"Scan completed. Number of devices found: {results.Count()}.");

                if (!results.Any())
                {
                    UpdateUI("No devices found. Try again?", true);
                }
                else
                {
                    foreach (var host in results)
                    {
                        Devices.Add(host);
                        Debug.WriteLine($"Found: {host.DisplayName} at {host.IPAddress}");
                    }
                    UpdateUI(actionName, true);
                }
            }
            catch (Exception ex)
            {
                UpdateUI("Error: " + ex.Message, true);
                Debug.WriteLine("Error searching for AirPlay devices: " + ex.Message);
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateUI(string content, bool isEnabled)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                searchButton.Content = content;
                searchButton.IsEnabled = isEnabled;
                refreshButton.IsEnabled = isEnabled;
            });
        }
    }
}
