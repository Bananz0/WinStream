using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;

namespace WinStream.Network
{
    public class DeviceInfo : INotifyPropertyChanged
    {
        // Basic Identification
        public string DisplayName { get; set; }
        public string DeviceName { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; }

        // Manufacturer and Model
        public string Manufacturer { get; set; }
        public string Model { get; set; }

        // Software and Firmware
        public string FirmwareVersion { get; set; }
        public string OSVersion { get; set; }

        // Network and Protocol
        public string BluetoothAddress { get; set; }
        public string DeviceID { get; set; }
        public string ProtocolVersion { get; set; }
        public string AirPlayVersion { get; set; }

        // Metadata
        public string SerialNumber { get; set; }
        public string PublicCUAirPlayPairingIdentity { get; set; }
        public string PublicCUSystemPairingIdentity { get; set; }
        public string PublicKey { get; set; }
        public RSAParameters? RsaPublicKey { get; set; }
        public string SupportedCodecs { get; set; }
        public string EncryptionTypes { get; set; }
        public string HouseholdID { get; set; }
        public string GroupUUID { get; set; }
        public bool IsGroupLeader { get; set; }
        public long RequiredSenderFeatures { get; set; }
        public long SystemFlags { get; set; }
        public string ToolTipText { get; set; }

        // Key type detection
        public bool HasRsaPublicKey => RsaPublicKey.HasValue;
        public bool HasEd25519PublicKey => !string.IsNullOrEmpty(PublicKey) && !HasRsaPublicKey;
        public bool IsAirPlay2Device => HasEd25519PublicKey;
        public bool SupportsAlac => SupportsCodec(1);
        public bool SupportsL16 => SupportsCodec(0);
        public bool SupportsUnencryptedRaop => SupportsEncryptionType(0);

        private bool SupportsCodec(int codecId)
        {
            if (string.IsNullOrWhiteSpace(SupportedCodecs))
            {
                return false;
            }

            return SupportedCodecs
                .Split(',')
                .Select(v => v.Trim())
                .Any(v => int.TryParse(v, out var parsed) && parsed == codecId);
        }

        private bool SupportsEncryptionType(int encryptionType)
        {
            if (string.IsNullOrWhiteSpace(EncryptionTypes))
            {
                return true;
            }

            return EncryptionTypes
                .Split(',')
                .Select(v => v.Trim())
                .Any(v => int.TryParse(v, out var parsed) && parsed == encryptionType);
        }

        /// <summary>
        /// Segoe MDL2 glyph selected by device model/name.
        /// </summary>
        public string DeviceTypeGlyph
        {
            get
            {
                var model = Model?.ToLowerInvariant() ?? "";
                var name = DisplayName?.ToLowerInvariant() ?? "";

                if (model.Contains("appletv") || name.Contains("apple tv"))
                    return "\uE7F4"; // Monitor/TV
                if (model.Contains("airport") || name.Contains("airport"))
                    return "\uEC27"; // Network tower

                return "\uE767"; // Volume/speaker (HomePod, Sonos, Bose, generic)
            }
        }

        // ── Observable UI state ──────────────────────────────────────────────

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            set
            {
                _isConnecting = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConnectingVisibility));
            }
        }

        private bool _isStreaming;
        public bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                _isStreaming = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConnectButtonText));
            }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        // Computed helpers for x:Bind
        public string ConnectButtonText => IsStreaming ? "Disconnect" : "Connect";
        public Visibility ConnectingVisibility => IsConnecting ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
