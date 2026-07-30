# WinStream

Tray-first **AirPlay audio sender** for Windows (WinUI 3 / .NET 8 / MSIX).

Stream system audio from a Windows PC to classic AirPlay / RAOP receivers (HomePod, Apple TV, AirPort Express, and many third-party speakers). The Store SKU uses **WASAPI loopback** — no kernel driver in the MSIX.

## Features

- Single-instance tray app (settings close hides; Quit exits)
- WASAPI loopback capture with render-endpoint picker + level meter
- Classic RAOP: RTSP handshake, ALAC, AES, RTP, sync/timing, volume
- Multi-room fan-out with Degraded / Reconnecting resilience
- Settings persistence; optional experimental AirPlay 2 **gate** (media path not production-ready)
- Optional virtual audio driver track **outside** Store (`drivers/winstream-vad/`)

## Requirements

- Windows 10 1809+ (x64 recommended)
- .NET 8 Windows Desktop / Windows App SDK (for building from source)
- Speakers/receivers that accept classic RAOP audio

## Build

```powershell
dotnet build WinStream.sln -c Release -p:Platform=x64
dotnet test WinStream.Tests -c Release
```

## Usage (short)

1. Run the app → tray icon appears.
2. Open settings → pick capture device → Discover → Connect.
3. Play audio on Windows; adjust stream volume in the app.
4. Quit from the tray menu when finished.

Full steps: [docs/user-guide.md](docs/user-guide.md)

## Packaging / Store

- Manifest: `WinStream/Package.appxmanifest`
- Capability justifications: [docs/store/capability-justifications.md](docs/store/capability-justifications.md)
- Device test matrix: [docs/testing/device-matrix.md](docs/testing/device-matrix.md)

**Do not** ship `.sys` drivers in the Store package.

## Project layout

| Path | Role |
|------|------|
| `WinStream/` | WinUI app, tray, WASAPI, RAOP session |
| `WinStream.Core/` | Testable protocol/audio/settings |
| `WinStream.Tests/` | xUnit tests |
| `docs/` | Plan, research, user guide, Store notes |
| `drivers/winstream-vad/` | Optional non-Store driver scaffold |

## Status

Code for the Store MVP pipeline (loopback + classic RAOP single/multi-room + packaging docs) is on branch `feat/winstream-full-product`. Manual device-matrix validation and Store Partner Center submission are still required before calling a build Store-ready. AirPlay 2 streaming and the virtual driver remain gated / optional stretch work.

## License

[LICENSE.txt](LICENSE.txt) (Unlicense).
