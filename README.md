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
- Off-by-default **WinStream Link** companion path (`tools/LinkRx`, `tools/LinkRx.Pi`) — not AirPlay, see below



## Requirements

- Windows 10 1809+ (x64 recommended)
- .NET 8 Windows Desktop / Windows App SDK (for building from source)
- Speakers/receivers that accept classic RAOP audio



## Build

```powershell
dotnet build WinStream.sln -c Release -p:Platform=x64
dotnet test WinStream.Tests -c Release
```



### VS Code / Cursor

Use the Run and Debug configs (`.vscode/launch.json`):


| Config                              | What it does                                                |
| ----------------------------------- | ----------------------------------------------------------- |
| **WinStream: Debug (Unpackaged)**   | Builds Debug x64 unpackaged and launches under the debugger |
| **WinStream: Release (Unpackaged)** | Same for Release                                            |
| **WinStream: Attach**               | Attach to a running `WinStream.exe`                         |


Tasks (Terminal → Run Task):


| Task                            | What it does                                            |
| ------------------------------- | ------------------------------------------------------- |
| `build-debug` / `build-release` | Unpackaged builds                                       |
| `build-and-install-release`     | Signed Release MSIX → trust cert → install (Start Menu) |
| `build-release-msix`            | Build/sign only (no install)                            |
| `ensure-package-certificate`    | Create/reuse PFX under secrets                          |




### Release install (signed, like a normal Windows app)

1. Copy `.env.example` → `.env` (already points at Sea Trials `.secrets`).
2. Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-and-install-release.ps1
```

This creates `WINSTREAM_SECRETS_DIR\windows\winstream-package.pfx` (once), builds a self-contained signed MSIX under `artifacts/msix/`, trusts the public CER (LocalMachine Root via one UAC prompt), and installs via `Add-AppxPackage`. Launch **WinStream** from the Start Menu afterward.

Signing password lives only in `.env` (`WINSTREAM_PACKAGE_CERTIFICATE_PASSWORD`) — never commit `.env` or `*.pfx`.

## Usage (short)

1. Run the app → tray icon appears.
2. Open settings → pick capture device → Discover → Connect.
3. Play audio on Windows; adjust stream volume in the app.
4. Quit from the tray menu when finished.

Full steps: [docs/user-guide.md](docs/user-guide.md)

## WinStream Link (experimental companion)

A separate companion path to a receiver you run yourself — **not** AirPlay, and it cannot reach a HomePod. Disabled unless `LinkFeatureEnabled` is set; when enabled it is mutually exclusive with AirPlay output. Run `tools/LinkRx` (Windows) or `tools/LinkRx.Pi` (Raspberry Pi + ALSA), then Scan or type the IP in the app. The UI only shows **8–10 ms** after a recorded Ethernet lab measurement; ordinary loopback and Wi‑Fi still stream without that claim.

The 8–10 ms average target is **not** validated: it requires the test-signed virtual audio driver and a wired-lab measurement per [docs/testing/link-e2e-measurement.md](docs/testing/link-e2e-measurement.md).

## Packaging / Store

- Manifest: `WinStream/Package.appxmanifest`
- Local signed sideload: `scripts/build-and-install-release.ps1` (uses `.env` + `WINSTREAM_SECRETS_DIR\windows\*.pfx`)
- Capability justifications: [docs/store/capability-justifications.md](docs/store/capability-justifications.md)
- Device test matrix: [docs/testing/device-matrix.md](docs/testing/device-matrix.md)

**Do not** ship `.sys` drivers in the Store package. Store submission still needs Partner Center publisher identity (separate from the local sideload cert).

## Project layout


| Path                     | Role                                    |
| ------------------------ | --------------------------------------- |
| `WinStream/`             | WinUI app, tray, WASAPI, RAOP session   |
| `WinStream.Core/`        | Testable protocol/audio/settings        |
| `WinStream.Tests/`       | xUnit tests                             |
| `docs/`                  | Plan, research, user guide, Store notes |
| `drivers/winstream-vad/` | Optional non-Store driver scaffold      |
| `tools/LinkRx*/`         | WinStream Link companion receivers      |




## Status

Code for the Store MVP pipeline (loopback + classic RAOP single/multi-room + packaging docs) is on branch `feat/winstream-full-product`. Manual device-matrix validation and Store Partner Center submission are still required before calling a build Store-ready. AirPlay 2 streaming and the virtual driver remain gated / optional stretch work.

## LicenseD

[LICENSE.txt](LICENSE.txt) (Unlicense).