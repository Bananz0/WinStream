# SysVAD upstream and license provenance

WinStream VAD is derived from Microsoft’s Windows driver sample:

- Repository: https://github.com/microsoft/Windows-driver-samples
- Source subtree: `audio/sysvad`
- Pinned upstream commit: `ef7c3074748ab05726c3a9161d3256118efd76e2`
- Retrieved: 2026-07-31
- Upstream license: MIT (`LICENSE` at the repository root)

## Vendoring rules

Phase 1 must:

1. Copy only the source required for the minimal x64 render endpoint.
2. Preserve Microsoft copyright and license headers.
3. Add the upstream MIT `LICENSE` alongside the vendored source.
4. Record any excluded sample components and local modifications below.
5. Update the pinned commit deliberately; never track an unpinned branch at build time.

## Intended exclusions

- PhoneAudio sample
- Keyword spotter, AEC, and unrelated APO samples
- Sample endpoints not needed by `WinStream Virtual Audio`
- DevCon binaries or other redistribution-only development tools

## Local modifications

None yet. Phase 1 will document the minimal endpoint selection, identity changes, INF isolation changes, and build-system changes here.
