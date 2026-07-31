#nullable disable

namespace WinStream.Core.Network
{
    /// <summary>
    /// The advertised receiver facts WinStream actually acts on: identity, address,
    /// what to show the user, and what decides the streaming protocol. Other TXT keys
    /// are deliberately not modelled until something consumes them.
    /// </summary>
    public class DeviceInfo
    {
        // Basic identification
        public string DisplayName { get; set; }
        public string IPAddress { get; set; }
        public int Port { get; set; }

        // Shown in the device list and details dialog
        public string Manufacturer { get; set; }
        public string Model { get; set; }

        // Identity and protocol selection
        public string DeviceID { get; set; }
        public string ProtocolVersion { get; set; }
        public string AirPlayVersion { get; set; }
        public string PublicCUAirPlayPairingIdentity { get; set; }
        public string PublicKey { get; set; }

        /// <summary>RAOP TXT <c>et</c> — encryption types (0=none, 1=RSA, 3/5=modern/other).</summary>
        public string EncryptionTypes { get; set; }

        public long Features { get; set; }
        public string FeaturesRaw { get; set; }
    }
}
