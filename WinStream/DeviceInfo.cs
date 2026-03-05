using System.Security.Cryptography;

namespace WinStream.Network
{
    public class DeviceInfo
    {
        // Basic Identification Information
        public string DisplayName { get; set; }
        public string DeviceName { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; }

        // Manufacturer and Model
        public string Manufacturer { get; set; }
        public string Model { get; set; }

        // Software and Firmware Versions
        public string FirmwareVersion { get; set; }
        public string OSVersion { get; set; }

        // Network and Protocol Information
        public string BluetoothAddress { get; set; }
        public string DeviceID { get; set; }
        public string ProtocolVersion { get; set; }
        public string AirPlayVersion { get; set; }

        // Additional Metadata
        public string SerialNumber { get; set; }
        public string PublicCUAirPlayPairingIdentity { get; set; }
        public string PublicCUSystemPairingIdentity { get; set; }
        public string PublicKey { get; set; }
        public RSAParameters? RsaPublicKey { get; set; } // Parsed RSA public key
        public string HouseholdID { get; set; }
        public string GroupUUID { get; set; }

        // Status and Features
        public bool IsGroupLeader { get; set; }
        public long RequiredSenderFeatures { get; set; }
        public long SystemFlags { get; set; }

        // Tooltip for displaying summarized info
        public string ToolTipText { get; set; }

        // Key type detection
        public bool HasRsaPublicKey => RsaPublicKey.HasValue;
        public bool HasEd25519PublicKey => !string.IsNullOrEmpty(PublicKey) && !HasRsaPublicKey;
        
        /// <summary>
        /// Indicates if the device is an AirPlay 2 device (uses Ed25519/Curve25519 keys)
        /// AirPlay 2 devices typically have Ed25519 public keys and require HomeKit pairing
        /// </summary>
        public bool IsAirPlay2Device => HasEd25519PublicKey;
    }
}
