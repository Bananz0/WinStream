# SimpleAudioSample upstream and license provenance

WinStream VAD is derived from Microsoft’s Windows driver sample:

- Repository: https://github.com/microsoft/Windows-driver-samples
- Source subtree: `audio/SimpleAudioSample`
- Pinned upstream commit: `ef7c3074748ab05726c3a9161d3256118efd76e2`
- Retrieved: 2026-07-31
- Upstream license: MIT (`LICENSE` at the repository root; copied here as `LICENSE`)

Earlier planning mentioned SysVAD. Phase 1 deliberately vendors **SimpleAudioSample** instead: it exposes a WaveRT render endpoint without SysVAD’s KS loopback pin that returns a sine tone. WinStream captures real mixed PCM via WASAPI software loopback against that render endpoint.

## Vendoring rules

1. Copy only the source required for the x64 virtual endpoint package.
2. Preserve Microsoft copyright and license headers.
3. Keep the upstream MIT `LICENSE` beside the vendored source.
4. Record excluded sample components and local modifications below.
5. Update the pinned commit deliberately; never track an unpinned branch at build time.

## Intended exclusions / leftovers

- SysVAD phone, keyword spotter, AEC, and APO samples (never copied)
- DevCon binaries
- Sample microphone-array capture endpoint remains in the source/INF for now as an unused leftover from SimpleAudioSample; WinStream capture uses the render endpoint only. A later cleanup can remove the mic array once detection/install paths are stable.

## Local modifications

- Renamed package/solution/output to `WinStreamVad`
- Root hardware ID: `ROOT\WINSTREAMVAD`
- Product GUID: `{E10CDFCF-3C10-45DE-B4B7-89DE1C73E15B}`
- Provider/manufacturer and endpoint friendly name: `WinStream` / `WinStream Virtual Audio`
- DriverVer baseline: `1.0.0.0`
- Componentized INF destination `DIRID 13` retained from the sample
- Internal C++ type names largely retain `SimpleAudioSample` prefixes to minimize churn; packaging and PnP identity use WinStream names
