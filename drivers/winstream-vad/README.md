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

1. Adapt the smallest render endpoint from Microsoft SysVAD.
2. Build an isolated, componentized driver package (`.inf`, `.sys`, `.cat`) outside the app project.
3. Install through the separate elevated `WinStream.DriverInstaller.exe`.
4. Detect the endpoint through its stable identity.
5. Reuse `WasapiLoopbackSource` against the virtual render endpoint.
6. Keep the user’s ordinary loopback endpoint unchanged and fall back to it whenever the virtual endpoint is unavailable.

See [UPSTREAM.md](UPSTREAM.md) for source and license provenance.

## Toolchain

The selected production toolchain is:

- Visual Studio 2026 (18.x)
- Matching Windows SDK/WDK 28000.2526 or newer 28000 servicing release
- Desktop development with C++ and driver workloads
- Spectre-mitigated libraries required by the selected WDK

This workstation currently has Visual Studio 2026 18.3 and SDK/WDK 10.0.26100.0. Phase 1 must install a matching VS 2026 WDK before accepting a clean driver build; do not mix SDK/WDK build numbers.

## Signing ladder

| Stage | Use |
|---|---|
| Test-signed + Windows TESTSIGNING | Development VMs only |
| Attestation-signed | Testing/sideload validation only; not retail Windows Update |
| HLK/WHCP + Microsoft-signed | Production public download and future Windows Update |

General Partner Center access is not enough. Production enablement requires Hardware program enrollment, EV identity, appropriate Entra Hardware roles, accepted agreements, HLK/WHCP, and a Microsoft-signed package.

Never commit certificates, private keys, `.sys`, `.cat`, packaged installers, CABs, or HLK output. The app and installer never read Partner Center credentials.

## Development installation

Phase 1 will document exact commands after the first clean build. Development installation is destructive and runs only in a disposable x64 VM:

1. Enable TESTSIGNING and reboot (observe Secure Boot/BitLocker precautions).
2. Install the test certificate into the VM test stores.
3. Run the separate elevated installer or approved SetupAPI/PnPUtil development command.
4. Validate `ROOT\WINSTREAMVAD` and the active `WinStream Virtual Audio` render endpoint.
5. Run the checklist and remove the device/package before restoring the VM snapshot.

Do not ship DevCon. The production installer uses SetupAPI and supported PnPUtil operations.

## App integration

`AppSettings.CaptureMode`:

| Value | Behavior |
|---|---|
| `Loopback` (default) | Capture the user-selected Windows render endpoint |
| `VirtualDriver` | Capture the detected WinStream render endpoint only after explicit user consent |

The app exposes no capture-method selector today: the setting stays `Loopback` until a
virtual-driver capture source exists, so the UI cannot offer a mode that does nothing.

The public Download action remains disabled until the production-signing follow-on plan is complete.

See `docs/testing/virtual-driver-checklist.md` and `docs/plan/2026-07-31-feat-virtual-audio-driver-install-plan.md`.
