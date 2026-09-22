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

1. Copy `.env.example` → `.env` and set `WINSTREAM_SECRETS_DIR` to your local secrets directory.
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

This branch (`feat/winstream-full-product`) is a **full-product integration** PR against [`bananz0/WinStream`](https://github.com/bananz0/WinStream) `master`. It turns the original RAOP discovery prototype into a tray-first Windows sender with classic multi-room AirPlay, an experimental AirPlay 2 path, optional virtual-driver work, and signed sideload packaging.

### What this PR delivers

| Area | Delivered |
| --- | --- |
| **Application shell** | Single-instance WinUI 3 tray app, settings persistence, branded MSIX packaging |
| **Capture** | WASAPI loopback with endpoint picker and level meter; optional event-driven capture for low-latency presets |
| **Classic RAOP** | RTSP, ALAC, AES, RTP, sync/timing, volume, multi-room fan-out with Degraded / Reconnecting states |
| **AirPlay 2 (gated)** | HKP pairing, encrypted RTSP, RECORD / ALAC RTP, PTP slave, prefer-AP2 routing when capable |
| **Pairing & passwords** | Persistent pairing store, AirPlay Receiver password prompts, RTSP Digest auth, keyed single-flight dialogs |
| **Discovery** | mDNS merge/retention so streaming receivers and password badges stay stable across passes |
| **Latency control** | **Auto** starts near ~50 ms and adjusts up/down under delivery pressure; **Extreme** is raise-only through a short ladder |
| **Live quality UI** | Exact buffer + measured send rate in the status pill; detailed metrics flyout while streaming |
| **Send path** | Absolute packet pacing, shared MMCSS Pro Audio elevation, pressure-window auto-latency |
| **Auto-connect** | Remember and reconnect the last receiver; honest failure copy for auth and network errors |
| **WinStream Link (off by default)** | Companion receiver tools (`tools/LinkRx`, `tools/LinkRx.Pi`); mutually exclusive with AirPlay output |
| **Virtual driver scaffold** | Non-Store driver source, installer UI, and release docs under `drivers/winstream-vad/` |
| **Local release install** | `scripts/build-and-install-release.ps1` — self-signed MSIX, cert trust, Start Menu install |
| **Tests** | **695** xUnit tests covering protocol, pacing, pairing, discovery, and UI copy helpers |

### Still pending (not blocking merge review)

| Item | Notes |
| --- | --- |
| **Device-matrix sign-off** | Manual validation on real Mac / HomePod / third-party RAOP targets ([device matrix checklist](docs/testing/device-matrix.md)) |
| **Microsoft Store submission** | Partner Center publisher identity and production signing — separate from local sideload cert |
| **AirPlay 2 production gate** | AP2 media path remains experimental until broader hardware soak passes |
| **Virtual audio driver release** | Test-signed / attestation build, WHQL or equivalent, GitHub Releases pipeline for `.sys` + installer |
| **WinStream Link SLA claim** | 8–10 ms UI claim requires wired-lab measurement with the virtual driver ([measurement gate](docs/testing/link-e2e-measurement.md)) |
| **Sub-50 ms end-to-end proof** | Virtual-driver capture path and Extreme/Auto tuning need sustained soak on target hardware |

### Branch hygiene

- Working plans, research, and code-review reports live under local `docs/` (gitignored).
- Secrets stay in `.env` + `WINSTREAM_SECRETS_DIR` — never committed.
- Test fixtures use fictional RFC 5737 addresses and device IDs only.

## License

[LICENSE.txt](LICENSE.txt) (Unlicense).