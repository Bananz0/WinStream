using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Zeroconf;

namespace WinStream.Network
{
    public static class DeviceDiscovery
    {
        private static readonly TimeSpan DefaultDiscoveryWindow = TimeSpan.FromMilliseconds(1800);
        private const string DiscoveryWindowMsEnvVar = "WINSTREAM_DISCOVERY_WINDOW_MS";

        private static readonly Dictionary<string, DeviceInfo> Devices = new();
        private static readonly Dictionary<string, int> DeviceMissCounts = new();
        private static readonly Dictionary<string, RSAParameters?> ParsedRsaKeyCache = new();
        private static readonly HashSet<string> LoggedUnsupportedKeyFingerprints = new();
        private static readonly object KeyCacheLock = new();
        private static CancellationTokenSource _cts;

        public static event EventHandler<List<DeviceInfo>> DevicesUpdated;
        public static event EventHandler<bool> DiscoveryStatusChanged;

        public static void StartDiscovery()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                throw new InvalidOperationException("Discovery is already running.");
            }

            _cts = new CancellationTokenSource();
            Task.Run(() => StartDiscoveryAsync(_cts.Token));
        }

        private static async Task StartDiscoveryAsync(CancellationToken cancellationToken)
        {
            DiscoveryStatusChanged?.Invoke(null, true);

            try
            {
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    var devices = await DiscoverDevicesAsync(linkedCts.Token);
                    DevicesUpdated?.Invoke(null, devices);
                    await Task.Delay(5000, linkedCts.Token); // Wait 5 seconds before next scan
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Discovery was canceled or timed out.");
            }
            finally
            {
                DiscoveryStatusChanged?.Invoke(null, false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        public static void StopDiscovery()
        {
            _cts?.Cancel();
        }

        public static List<DeviceInfo> GetKnownDevicesSnapshot()
        {
            return Devices.Values.ToList();
        }

        internal static async Task<List<DeviceInfo>> DiscoverDevicesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var discoveryWindow = ResolveDiscoveryWindow();
                var raopTask = SafeResolveAsync("_raop._tcp.local.", discoveryWindow, cancellationToken);
                var airplayTask = SafeResolveAsync("_airplay._tcp.local.", discoveryWindow, cancellationToken);
                await Task.WhenAll(raopTask, airplayTask);

                var raopResults = raopTask.Result;
                var airplayResults = airplayTask.Result;

                var currentDevices = new List<DeviceInfo>();
                foreach (var host in raopResults)
                {
                    var ipAddress = host.IPAddresses?.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(ipAddress))
                    {
                        continue;
                    }

                    var service = host.Services?.FirstOrDefault().Value;
                    if (service == null || service.Port <= 0)
                    {
                        continue;
                    }

                    var publicKey = GetTxtRecordValue(host, "pk");
                    var matchingAirPlayHost = airplayResults.FirstOrDefault(h => h.IPAddresses.Contains(ipAddress));
                    currentDevices.Add(new DeviceInfo
                    {
                        DisplayName = ExtractDeviceName(host, matchingAirPlayHost),
                        IPAddress = ipAddress,
                        Port = service.Port,
                        ToolTipText = $"IP Address: {ipAddress}",
                        Manufacturer = GetTxtRecordValue(host, "manufacturer"),
                        Model = GetTxtRecordValue(host, "model"),
                        FirmwareVersion = GetTxtRecordValue(host, "fv"),
                        OSVersion = GetTxtRecordValue(host, "osvers"),
                        BluetoothAddress = GetTxtRecordValue(host, "btaddr"),
                        DeviceID = GetTxtRecordValue(host, "deviceid"),
                        ProtocolVersion = GetTxtRecordValue(host, "protovers"),
                        AirPlayVersion = GetTxtRecordValue(host, "srcvers"),
                        SerialNumber = GetTxtRecordValue(host, "serialNumber"),
                        PublicCUAirPlayPairingIdentity = GetTxtRecordValue(host, "pi"),
                        PublicCUSystemPairingIdentity = GetTxtRecordValue(host, "psi"),
                        PublicKey = publicKey,
                        RsaPublicKey = ParseRsaPublicKey(publicKey),
                        SupportedCodecs = GetTxtRecordValue(host, "cn"),
                        EncryptionTypes = GetTxtRecordValue(host, "et"),
                        RawTxtRecords = GetTxtRecords(host, matchingAirPlayHost),
                        HouseholdID = GetTxtRecordValue(host, "hmid"),
                        GroupUUID = GetTxtRecordValue(host, "gid"),
                        IsGroupLeader = TryParseBoolean(GetTxtRecordValue(host, "igl")),
                        RequiredSenderFeatures = TryParseLong(GetTxtRecordValue(host, "rsf")),
                        SystemFlags = TryParseLong(GetTxtRecordValue(host, "sf"), GetTxtRecordValue(host, "flags"))
                    });
                }

                ProcessDiscoveredDevices(currentDevices);
                return Devices.Values.ToList();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Device discovery operation was canceled due to timeout.");
                return GetKnownDevicesSnapshot();
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Discovery failed: {ex.Message}", "discovery");
                return GetKnownDevicesSnapshot();
            }
        }

        private static async Task<IReadOnlyList<IZeroconfHost>> SafeResolveAsync(string serviceType, TimeSpan window, CancellationToken cancellationToken)
        {
            try
            {
                var resolveTask = ZeroconfResolver.ResolveAsync(serviceType, window);
                if (!cancellationToken.CanBeCanceled)
                {
                    return await resolveTask;
                }

                var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
                var completed = await Task.WhenAny(resolveTask, cancelTask);
                if (completed == cancelTask)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return await resolveTask;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                // Zeroconf may cancel internally when no response is received in the
                // discovery window. Treat this as "no results" instead of hard failure.
                return Array.Empty<IZeroconfHost>();
            }
            catch (Exception ex)
            {
                Logger.LogMessage($"Resolve failed for {serviceType}: {ex.Message}", "discovery");
                return Array.Empty<IZeroconfHost>();
            }
        }

        private static TimeSpan ResolveDiscoveryWindow()
        {
            var raw = Environment.GetEnvironmentVariable(DiscoveryWindowMsEnvVar);
            if (int.TryParse(raw, out var ms) && ms >= 250 && ms <= 10000)
            {
                return TimeSpan.FromMilliseconds(ms);
            }

            return DefaultDiscoveryWindow;
        }

        private static void ProcessDiscoveredDevices(List<DeviceInfo> currentDevices)
        {
            var currentDeviceAddresses = currentDevices.Select(d => d.IPAddress).ToHashSet();

            foreach (var device in currentDevices)
            {
                if (!Devices.ContainsKey(device.IPAddress))
                {
                    Devices[device.IPAddress] = device;
                }
                DeviceMissCounts[device.IPAddress] = 0; // Reset miss count
            }

            foreach (var deviceIp in Devices.Keys.ToList())
            {
                if (!currentDeviceAddresses.Contains(deviceIp))
                {
                    DeviceMissCounts[deviceIp]++;
                    if (DeviceMissCounts[deviceIp] >= 3)
                    {
                        Devices.Remove(deviceIp);
                        DeviceMissCounts.Remove(deviceIp);
                    }
                }
            }
        }

        private static string GetTxtRecordValue(IZeroconfHost host, string key)
        {
            foreach (var service in host.Services.Values)
            {
                if (service.Properties == null) continue;

                foreach (var record in service.Properties)
                {
                    if (record.TryGetValue(key, out var value))
                    {
                        return value;
                    }
                }
            }
            return string.Empty;
        }

        private static string[] GetTxtRecords(params IZeroconfHost[] hosts)
        {
            var records = new List<string>();

            foreach (var host in hosts)
            {
                if (host?.Services == null)
                {
                    continue;
                }

                foreach (var service in host.Services.Values)
                {
                    if (service.Properties == null)
                    {
                        continue;
                    }

                    foreach (var record in service.Properties)
                    {
                        foreach (var pair in record)
                        {
                            if (string.IsNullOrWhiteSpace(pair.Key))
                            {
                                continue;
                            }

                            records.Add($"{pair.Key}={pair.Value}");
                        }
                    }
                }
            }

            return records
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TryParseBoolean(string value)
        {
            return bool.TryParse(value, out var result) ? result : false;
        }

        private static long TryParseLong(params string[] values)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var normalized = value.Trim();
                if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(normalized[2..], System.Globalization.NumberStyles.HexNumber, null, out var hexResult))
                {
                    return hexResult;
                }

                if (long.TryParse(normalized, out var result))
                {
                    return result;
                }
            }

            return 0;
        }

        private static void PrintDeviceInfo(DeviceInfo device)
        {
            Console.WriteLine($"Device Name: {device.DeviceName}");
            Console.WriteLine($"Display Name: {device.DisplayName}");
            Console.WriteLine($"IP Address: {device.IPAddress}");
            Console.WriteLine($"Port: {device.Port}");
            Console.WriteLine($"Manufacturer: {device.Manufacturer}");
            Console.WriteLine($"Model: {device.Model}");
            Console.WriteLine($"Firmware Version: {device.FirmwareVersion}");
            Console.WriteLine($"OS Version: {device.OSVersion}");
            Console.WriteLine($"Bluetooth Address: {device.BluetoothAddress}");
            Console.WriteLine($"Device ID: {device.DeviceID}");
            Console.WriteLine($"Protocol Version: {device.ProtocolVersion}");
            Console.WriteLine($"AirPlay Version: {device.AirPlayVersion}");
            Console.WriteLine($"Serial Number: {device.SerialNumber}");
            Console.WriteLine($"Public CU AirPlay Pairing Identity: {device.PublicCUAirPlayPairingIdentity}");
            Console.WriteLine($"Public CU System Pairing Identity: {device.PublicCUSystemPairingIdentity}");
            Console.WriteLine($"Public Key: {device.PublicKey}");
            Console.WriteLine($"Supported Codecs (cn): {device.SupportedCodecs}");
            Console.WriteLine($"Encryption Types (et): {device.EncryptionTypes}");
            Console.WriteLine($"Household ID: {device.HouseholdID}");
            Console.WriteLine($"Group UUID: {device.GroupUUID}");
            Console.WriteLine($"Is Group Leader: {device.IsGroupLeader}");
            Console.WriteLine($"Required Sender Features: {device.RequiredSenderFeatures}");
            Console.WriteLine($"System Flags: {device.SystemFlags}");
            Console.WriteLine($"Tooltip Text: {device.ToolTipText}");
            Console.WriteLine();
        }

        private static string ExtractDeviceName(IZeroconfHost raopHost, IZeroconfHost airplayHost)
        {
            return airplayHost?.DisplayName.Split('@').FirstOrDefault() ?? raopHost.DisplayName;
        }

        private static RSAParameters? ParseRsaPublicKey(string base64PublicKey)
        {
            if (string.IsNullOrEmpty(base64PublicKey))
            {
                return null;
            }

            lock (KeyCacheLock)
            {
                if (ParsedRsaKeyCache.TryGetValue(base64PublicKey, out var cached))
                {
                    return cached;
                }
            }

            try
            {
                // The pk field contains a base64-encoded public key
                var publicKeyBytes = Convert.FromBase64String(base64PublicKey);
                Debug.WriteLine($"Raw public key length: {publicKeyBytes.Length} bytes");
                
                // Check if this is an Ed25519/Curve25519 key (32 bytes) - AirPlay 2 devices
                // Ed25519 public keys are exactly 32 bytes
                if (publicKeyBytes.Length == 32)
                {
                    Debug.WriteLine("Detected non-RSA public key (32 bytes), likely AirPlay 2.");
                    LogUnsupportedKeyOnce(base64PublicKey,
                        "Device uses non-RSA public key (32 bytes), likely AirPlay 2. WinStream will use unencrypted mode.");
                    // Return null for RSA since this device uses Ed25519, not RSA
                    // AirPlay 2 devices require HomeKit pairing protocol instead of RSA encryption
                    return CacheRsaPublicKeyResult(base64PublicKey, null);
                }
                
                // RSA keys are typically 256+ bytes for 2048-bit keys
                if (publicKeyBytes.Length < 128)
                {
                    Debug.WriteLine($"Detected non-RSA public key ({publicKeyBytes.Length} bytes).");
                    LogUnsupportedKeyOnce(base64PublicKey,
                        $"Device uses non-RSA public key ({publicKeyBytes.Length} bytes). WinStream will use unencrypted mode.");
                    return CacheRsaPublicKeyResult(base64PublicKey, null);
                }
                
                using var rsa = RSA.Create();
                
                // Try different import methods for RSA keys
                
                // Method 1: Try RSAPublicKey format (PKCS#1 public key)
                try
                {
                    rsa.ImportRSAPublicKey(publicKeyBytes, out _);
                    Debug.WriteLine("Successfully parsed RSA key using ImportRSAPublicKey (PKCS#1)");
                    Logger.LogMessage("Successfully parsed RSA public key using PKCS#1 format", "authentication");
                    return CacheRsaPublicKeyResult(base64PublicKey, rsa.ExportParameters(false));
                }
                catch (Exception ex1)
                {
                    Debug.WriteLine($"PKCS#1 import failed: {ex1.Message}");
                    
                    // Method 2: Try SubjectPublicKeyInfo format (X.509)
                    try
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);
                        Debug.WriteLine("Successfully parsed RSA key using ImportSubjectPublicKeyInfo (X.509)");
                        Logger.LogMessage("Successfully parsed RSA public key using X.509 format", "authentication");
                        return CacheRsaPublicKeyResult(base64PublicKey, rsa.ExportParameters(false));
                    }
                    catch (Exception ex2)
                    {
                        Debug.WriteLine($"X.509 import failed: {ex2.Message}");
                        LogUnsupportedKeyOnce(base64PublicKey,
                            $"Device key is not RSA (PKCS#1 and X.509 import failed). WinStream will use unencrypted mode.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to decode public key: {ex.Message}");
                LogUnsupportedKeyOnce(base64PublicKey, $"Public key decode failed: {ex.Message}");
            }
            
            return CacheRsaPublicKeyResult(base64PublicKey, null);
        }

        private static RSAParameters? CacheRsaPublicKeyResult(string base64PublicKey, RSAParameters? result)
        {
            lock (KeyCacheLock)
            {
                ParsedRsaKeyCache[base64PublicKey] = result;
            }

            return result;
        }

        private static void LogUnsupportedKeyOnce(string base64PublicKey, string message)
        {
            string fingerprint;
            try
            {
                var normalizedKeyBytes = Convert.FromBase64String(base64PublicKey);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(normalizedKeyBytes);
                fingerprint = Convert.ToHexString(hash);
            }
            catch
            {
                fingerprint = base64PublicKey;
            }

            lock (KeyCacheLock)
            {
                if (!LoggedUnsupportedKeyFingerprints.Add(fingerprint))
                {
                    return;
                }
            }

            Logger.LogMessage(message, "authentication");
        }
    }
}
