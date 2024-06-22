# WinStream

## AirPlay Audio Sender for Windows

Stream audio seamlessly from your Windows PC to a wide range of AirPlay-compatible devices, including HomePod, Bose speakers, Sonos systems, Airport Express, iMacs, and more.

### Key Features

* **Effortless AirPlay Integration:** Send audio directly from your Windows applications to AirPlay receivers on your network.
* **Virtual Audio Device Creation:** Capture system-wide audio using the dedicated WinStream virtual audio device for comprehensive streaming capabilities.
* **Broad Receiver Compatibility:** Enjoy the flexibility of streaming to various AirPlay-enabled devices, enhancing your audio experience across diverse ecosystems.
* **Simplified Setup and Configuration:** WinStream prioritizes ease of use, ensuring a smooth setup process and intuitive configuration options.

### How It Works

WinStream operates by creating a virtual audio device on your Windows system. This device acts as a bridge, capturing the system audio and transmitting it to AirPlay receivers discovered on your network. The captured audio stream is then played back on the chosen AirPlay device, delivering your desired audio content.

### Installation

[TODO]

### Usage

1. **Selecting WinStream Audio Device:**
   * Access your system's audio settings (e.g., Sound control panel).
   * Locate and choose "WinStream" as the default or desired audio output device.

2. **Connecting to AirPlay Receivers:**
   * Open any audio application on your Windows PC (e.g., music player, web browser).
   * Initiate playback of your desired audio content.
   * Look for the AirPlay playback destination option within the application (the specific location might vary depending on the application).
   * Select the AirPlay receiver you want to stream to from the available list.

### Requirements

* Windows 

### Building from Source

[TODO]

### Contributing

We welcome contributions from the community! If you'd like to enhance WinStream, feel free to submit a Pull Request outlining your changes. We appreciate your collaboration in making this project even better.

### Acknowledgements

The development of WinStream benefited greatly from the following valuable resources:

* [Zeroconf](https://github.com/novotnyllc/Zeroconf): Provides Bonjour support for .NET environments, enabling AirPlay service discovery.
* [AirPlay Protocol Documentation](https://nto.github.io/AirPlay.html): Offers a comprehensive explanation of the AirPlay protocol, aiding in implementation.
* [Airtunes2 Protocol Documentation](https://git.zx2c4.com/Airtunes2): Delves into AirPlay conventions and message formats, crucial for accurate audio transmission.
* [Emanuel Cozzi](https://emanuelecozzi.net/docs/airplay2/): Serves as an inspiration for AirPlay development efforts.
### License

WinStream is released under the MIT License. See the [LICENSE](LICENSE) file for details.

### Disclaimer

WinStream is an independent project and is not affiliated with, authorized, maintained, sponsored, or endorsed by Apple Inc. or any of its related entities. The use of the AirPlay protocol is entirely at your own discretion and responsibility.