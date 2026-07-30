# WinStream virtual audio driver (optional)

This track is **optional** and **must not** ship inside the Microsoft Store MSIX package.

## Why separate?

MSIX cannot install kernel-mode drivers. The Store SKU captures system audio with **WASAPI loopback** on a render endpoint. A selectable “WinStream” output device requires a WHCP/Windows Update (or sideload) driver package.

## Intended approach

1. Fork/adapt Microsoft **SysVAD** (or equivalent virtual audio miniport) under this folder.
2. Produce a signed driver package (`.inf` + `.sys`) distributed outside Store.
3. App settings expose `CaptureMode = VirtualDriver` when the endpoint is present; otherwise fall back to loopback.

## Current status

Scaffolding only. No `.sys` is checked in. Do not add kernel binaries to the MSIX project.

## Build prerequisites (when implemented)

- Windows Driver Kit (WDK) matching the target Windows version
- Visual Studio with Desktop + driver workloads
- Test signing or attestation / EV signing for distribution

## App integration

`AppSettings.CaptureMode`:

| Value | Behavior |
|-------|----------|
| `Loopback` (default) | WASAPI loopback on selected render device (Store path) |
| `VirtualDriver` | Prefer the WinStream render endpoint if installed; else error with install guidance |

See `docs/testing/virtual-driver-checklist.md`.
