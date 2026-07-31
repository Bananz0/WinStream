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

Build with the repo script, which restores the packages and picks the right MSBuild:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-winstream-vad.ps1
```

Package output lands under `drivers\winstream-vad\x64\Release\package\` (`WinStreamVad.sys`, `.inf`, `.cat`) plus a build manifest at `artifacts\driver\build-manifest.json`. Inf2Cat targets Windows 11+ (`10_CO_X64` and later) to match the INF’s `NTamd64.10.0...22000` decoration.

**Use the 64-bit MSBuild.** This is a correctness requirement, not a preference:

```powershell
# Correct — <VS>\MSBuild\Current\Bin\amd64\MSBuild.exe
nuget restore drivers\winstream-vad\packages.config -PackagesDirectory drivers\winstream-vad\packages
& "$vs\MSBuild\Current\Bin\amd64\MSBuild.exe" drivers\winstream-vad\WinStreamVad.sln `
    /p:Configuration=Release /p:Platform=x64 /p:SignMode=Off
```

The 32-bit MSBuild on `Bin\` makes the driver targets resolve x86 copies of `InfVerif.dll` and `ApiValidator.exe`. The `Microsoft.Windows.WDK.x64` package ships neither, so the build emits `WinStreamVad.sys`, then fails INF verification and catalog generation — a confusing "the driver built but the package didn't" state.

A full WDK installation is not required; the WDK arrives through the NuGet packages in `packages.config`. Visual Studio with the C++ toolset is enough.

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
2. Build a test-signed package and install it from an elevated prompt:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-winstream-vad.ps1 -TestSign
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-winstream-vad.ps1
```

The install script refuses to run without elevation or TESTSIGNING, stops when Secure Boot is on (it overrides TESTSIGNING), and warns when HVCI is running, since that lets the package install while the driver silently fails to load.

3. Validate Device Manager shows `ROOT\WINSTREAMVAD` and a render endpoint named `WinStream Virtual Audio`.
4. Measure the endpoint rather than trusting it:

```powershell
dotnet run --project tools\VadProbe\VadProbe.csproj -c Release -- --seconds 60
```

5. Run `docs/testing/virtual-driver-checklist.md`, then uninstall and restore the snapshot:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-winstream-vad.ps1 -Uninstall
```

### Packet-size constraints

The render WaveRT interface advertises `DEVPKEY_KsAudio_PacketSize_Constraints2`
(`ksmedia.h`, Windows 10 1607+) on the wave filter's `KSCATEGORY_AUDIO` interface
before `PcRegisterSubdevice`. The DEFAULT processing mode declares 144 frames —
3 ms at the fixed 48 kHz format — over a **1 ms transport minimum**.

The minimum must stay strictly below the per-mode packet size. Windows ignores a
mode constraint that is not higher than the transport minimum, so the earlier
3 ms/3 ms pairing would have silently discarded the 3 ms claim and left the
endpoint at the 10 ms default. SYSVAD uses the same shape: a 2 ms minimum under a
128-sample DEFAULT mode.

Declaring a period the driver cannot actually service does not produce a polite
fallback — it produces glitches. Low-latency operation also requires registering
driver-owned threads through `PcAddStreamResource` so Windows can isolate them.

That property is a **claim the driver makes**, not a measurement. `VadProbe`
reports it as `Declared`, separately from the `Measured` callback p95, and only
the measured number can satisfy `LinkSlaEligibility`. For comparison, an ordinary
AMD HD Audio endpoint on the dev machine declares a 10 ms minimum period and
measures 64 ms loopback p95.

### Validating a 3 ms period

Two signals together, neither sufficient alone:

1. `VadProbe` — measured WASAPI callback cadence at the requested period.
2. A glitch-free trace over the same run:

```powershell
wpr -start Media.wprp -filemode
# run the VadProbe soak
wpr -stop vad-3ms.etl
```

Open the trace in Media eXperience Analyzer and check the Audio Glitches view;
`Microsoft-Windows-Audio` is the authoritative provider. Enable Driver Verifier
against `WinStreamVad.sys` on the lab VM while testing.

Do not ship DevCon. The production installer uses SetupAPI and supported PnPUtil operations.

## App integration

`AppSettings.CaptureMode`:

| Value | Behavior |
|---|---|
| `Loopback` (default) | Capture the user-selected Windows render endpoint |
| `VirtualDriver` | Capture the detected WinStream render endpoint only after explicit user consent |

The public Download action remains disabled until the production-signing follow-on plan is complete.

See `docs/testing/virtual-driver-checklist.md` and `docs/plan/2026-07-31-feat-virtual-audio-driver-install-plan.md`.
