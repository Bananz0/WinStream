# WinStream

## AirPlay Audio Sender for Windows

Stream audio seamlessly from your Windows PC to a wide range of AirPlay-compatible devices, including HomePod, Bose speakers, Sonos systems, Airport Express, iMacs, and more.

### Key Features

* **Effortless AirPlay Integration:** Send audio directly from your Windows applications to AirPlay receivers on your network.
* **Virtual Audio Device:** Capture system-wide audio using the dedicated WinStream virtual audio device for comprehensive streaming capabilities.
* **Broad Receiver Compatibility:** Stream to various AirPlay-enabled devices, enhancing your audio experience across diverse ecosystems.
* **Simplified Setup:** User-friendly installation and intuitive configuration options prioritize ease of use.
* **Volume Control:** Adjust audio levels directly from Windows, seamlessly integrating with your system's audio controls.
* **Low Latency:** Optimized for minimal delay, ensuring your audio stays in sync.

### How It Works

WinStream creates a virtual audio device on your Windows system, acting as a bridge to capture system audio and transmit it to AirPlay receivers on your network. The captured audio stream is then played back on the chosen AirPlay device in real-time.

### Installation

1. Download the latest release from the [Releases](https://github.com/yourusername/WinStream/releases) page.
2. Run the installer and follow the on-screen instructions.
3. Restart your computer to ensure all components are properly initialized.

### Usage

1. **Selecting WinStream Audio Device:**
   * Open Windows Sound settings (right-click the speaker icon in the system tray).
   * Under "Choose your output device," select "WinStream Virtual Audio Device."

2. **Connecting to AirPlay Receivers:**
   * Open the WinStream application from your Start menu or desktop.
   * Click "Scan for Devices" to discover available AirPlay receivers.
   * Select your desired AirPlay device from the list.
   * Click "Connect" to start streaming.

3. **Adjusting Volume:**
   * Use Windows volume controls as normal - WinStream will respect these settings.
   * Fine-tune volume within the WinStream application if needed.

### Requirements

* Windows 10 (64-bit) or later
* .NET Framework 4.7.2 or higher

### Building from Source

1. Clone the repository: `git clone https://github.com/yourusername/WinStream.git`
2. Open the solution in Visual Studio 2019 or later.
3. Build the solution in Release mode.
4. The driver components require the Windows Driver Kit (WDK) to compile.

### Contributing

We welcome contributions! Please see our [Contributing Guidelines](CONTRIBUTING.md) for more information on how to get involved.

### Acknowledgements

WinStream's development was greatly aided by these valuable resources:

* [Zeroconf](https://github.com/novotnyllc/Zeroconf): Bonjour support for .NET, enabling AirPlay service discovery.
* [AirPlay Protocol Documentation](https://nto.github.io/AirPlay.html): Comprehensive explanation of the AirPlay protocol.
* [Airtunes2 Protocol Documentation](https://git.zx2c4.com/Airtunes2): Details on AirPlay conventions and message formats.
* [Emanuel Cozzi's AirPlay2 Documentation](https://emanuelecozzi.net/docs/airplay2/): Inspiration for AirPlay development efforts.

### License

WinStream is released under The Unlicense. This means you can do whatever you want with this software. For more information, please see the [LICENSE](LICENSE) file or visit [unlicense.org](https://unlicense.org).

### Disclaimer

WinStream is an independent project and is not affiliated with, authorized, maintained, sponsored, or endorsed by Apple Inc. or any of its affiliates. The use of the AirPlay protocol is entirely at your own discretion and responsibility.
