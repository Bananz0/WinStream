# WinStream virtual audio driver (optional)

This component is optional and **must never ship inside the Microsoft Store MSIX**. The Store app remains fully functional through WASAPI loopback.

## Frozen v1 identity

These values are the compatibility contract between the INF, installer, endpoint detector, tests, and documentation. Do not regenerate them after Phase 0.

| Field | Value |
|---|---|
| Root hardware ID | `ROOT\WINSTREAMVAD` |
| Product/device interface GUID | `{E10CDFCF-3C10-45DE-B4B7-89DE1C73E15B}` |
| Endpoint property-set GUID | `{7F9A6486-77B7-4823-B9C8-8F3BBCDEBAA2}` |
| Provider/manufacturer | `WinStream` |
| Endpoint friendly name | `WinStream Virtual Audio` |
| Driver package name | `WinStreamVad` |
| Initial version | `1.0.0.0` |
| First support matrix | Windows 10/11 Desktop x64 |
| Release repository | `bananz0/WinStream` GitHub Releases |

The friendly name is display-only. Detection must validate the hardware ID and product/interface property; WASAPI endpoint IDs are opaque.

## Architecture and capture behavior

1. Adapt Microsoft **SimpleAudioSample** (render WaveRT endpoint; no KS loopback sine generator).
2. Build an isolated, componentized driver package (`.inf`, `.sys`, `.cat`) outside the app project.
3. Install through the separate elevated `WinStream.DriverInstaller.exe` (Phase 5) or a development TESTSIGNING install.
4. Detect the endpoint through its stable identity.
5. Reuse `WasapiLoopbackSource` against the virtual render endpoint.
6. Keep the user’s ordinary loopback endpoint unchanged and fall back to it whenever the virtual endpoint is unavailable.

See [UPSTREAM.md](UPSTREAM.md) for source and license provenance.

## Toolchain

- Visual Studio 2026 (18.8+) with **Windows Driver Kit** individual component (`Component.Microsoft.Windows.DriverKit`)
- Matching Windows SDK/WDK 10.0.28000.x
- Desktop development with C++ and Spectre-mitigated libraries

Build from a VS Developer PowerShell (NuGet WDK packages under `drivers/winstream-vad/packages/` must be restored first):

```powershell
nuget restore drivers\winstream-vad\packages.config -PackagesDirectory drivers\winstream-vad\packages
msbuild drivers\winstream-vad\WinStreamVad.sln /p:Configuration=Release /p:Platform=x64 /p:SignMode=Off
```

Package output lands under `drivers\winstream-vad\x64\Release\package\` (`WinStreamVad.sys`, `.inf`, `.cat`). Inf2Cat targets Windows 11+ (`10_CO_X64` and later) to match the INF’s `NTamd64.10.0...22000` decoration.

## Signing ladder

| Stage | Use |
|---|---|
| Test-signed + Windows TESTSIGNING | Development VMs only |
| Attestation-signed | Testing/sideload validation only; not retail Windows Update |
| HLK/WHCP + Microsoft-signed | Production public download and future Windows Update |

Never commit certificates, private keys, `.sys`, `.cat`, packaged installers, CABs, or HLK output.

## Development installation

Destructive; use a disposable x64 VM with a snapshot:

1. Enable TESTSIGNING and reboot (`bcdedit /set testsigning on`).
2. From an elevated prompt in the package folder:

```powershell
pnputil /add-driver WinStreamVad.inf /install
```

3. Validate Device Manager shows `ROOT\WINSTREAMVAD` and a render endpoint named `WinStream Virtual Audio`.
4. Run `docs/testing/virtual-driver-checklist.md`, then remove the device/package and restore the VM snapshot.

Do not ship DevCon. The production installer uses SetupAPI and supported PnPUtil operations.

## App integration

`AppSettings.CaptureMode`:

| Value | Behavior |
|---|---|
| `Loopback` (default) | Capture the user-selected Windows render endpoint |
| `VirtualDriver` | Capture the detected WinStream render endpoint only after explicit user consent |

The public Download action remains disabled until the production-signing follow-on plan is complete.

See `docs/testing/virtual-driver-checklist.md` and `docs/plan/2026-07-31-feat-virtual-audio-driver-install-plan.md`.
