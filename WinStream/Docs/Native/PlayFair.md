# winstream-playfair.dll — sender-side FairPlay shim

WinStream's AirPlay 2 media SETUP can advertise FairPlay-encrypted audio frames
(`et=32`, `ekey`, `eiv`) to receivers that require it (notably modern Apple TV
and HomePod). The FairPlay handshake itself is Apple-proprietary; WinStream does
not ship an implementation. Instead, `PlayFair.cs` P/Invokes into a native DLL
named **`winstream-playfair.dll`** placed next to `WinStream.exe`. When the DLL
is absent or fails to load, WinStream falls back to a non-FairPlay session
(`et=0`, no `ekey`/`eiv`, plaintext RTP audio frames) — which works on
non-Apple AirPlay 2 receivers and on Apple receivers configured for "Anyone on
the Same Network" without FairPlay enforcement.

## ABI

The DLL must export four C-callable functions with `cdecl` calling convention:

```c
// Allocate a FairPlay session handle. Return 0 on success, non-zero on error.
int wsfp_create(void** out_handle);

// Given the 142-byte M2 challenge from the receiver's POST /fp-setup response,
// produce the 164-byte M3 request body the sender will POST back. Returns 0
// on success.
int wsfp_setup_response(void* handle,
                        const unsigned char m2_in[142],
                        unsigned char       m3_out[164]);

// After the M3 round trip, given the 32-byte FairPlay response payload from the
// receiver and the 16-byte AES key the sender will use to encrypt audio, fill
// out the 72-byte ekey blob that goes into the SETUP plist. Returns 0 on
// success.
int wsfp_decrypt_key(void* handle,
                     const unsigned char fp_response_in[32],
                     const unsigned char aes_key_in[16],
                     unsigned char       ekey_out[72]);

// Free the session handle.
void wsfp_destroy(void* handle);
```

All sizes are fixed and validated by `PlayFair.cs` before the call. The DLL
must match the architecture of the running WinStream process (`x64` for a
`win-x64` build, `ARM64` for a `win-arm64` build, etc.).

## Reference implementation

A FairPlay-capable C source widely used by the open-source community ships with
**Shairport-Sync** (`mikebrady/shairport-sync`) and the historical **playfair**
project. Shairport-Sync is GPL-3.0; if you build `winstream-playfair.dll` from
that source, you must comply with the GPL terms (source availability, license
text, attribution).

A typical wrapper layout:

```
native/winstream-playfair/
├── CMakeLists.txt
├── playfair.c             // from shairport-sync/playfair (GPL-3.0)
├── playfair.h
├── modified_md5.c
├── modified_md5.h
├── hand_garble.c
├── sap_hash.c
├── playfair_aes.c
└── shim.c                 // implements wsfp_* exports on top of playfair.c
```

`shim.c` glues the public `wsfp_*` functions to the underlying `fairplay_*`
calls. The mapping is direct: `wsfp_setup_response` calls the playfair function
that consumes 142 bytes and produces 164 bytes; `wsfp_decrypt_key` calls the
function that wraps an AES key against the post-M3 FairPlay session.

### CMake outline

```cmake
cmake_minimum_required(VERSION 3.20)
project(winstream-playfair LANGUAGES C)
set(CMAKE_C_STANDARD 11)
add_library(winstream-playfair SHARED
    playfair.c modified_md5.c hand_garble.c sap_hash.c playfair_aes.c shim.c)
set_target_properties(winstream-playfair PROPERTIES
    PREFIX "" OUTPUT_NAME "winstream-playfair")
```

Build (Visual Studio Developer PowerShell):

```powershell
cmake -S native/winstream-playfair -B build -A x64
cmake --build build --config Release
copy build\Release\winstream-playfair.dll WinStream\bin\x64\Debug\net8.0-windows10.0.19041.0\
```

## Attribution

When distributing `winstream-playfair.dll` built from Shairport-Sync sources,
include the upstream `LICENSES` and `COPYING` files alongside the DLL, and
credit Mike Brady and the Shairport-Sync contributors. The reverse-engineered
FairPlay tables originate from the broader open-source receiver community —
preserve all original copyright notices in the source files.

WinStream itself does not include any FairPlay code or tables; it only provides
the C# loader and the public ABI documented above.

## Verification

When the DLL is present, the authentication log shows:

```
AirPlay 2 fp-setup M1: RTSP/1.0 200 OK bodyLen=142
AirPlay 2 fp-setup M3: RTSP/1.0 200 OK bodyLen=32
AirPlay 2 media session established (codec=L16, fairPlay=True).
```

When the DLL is missing:

```
FairPlay disabled: winstream-playfair.dll not found. Build it from a FairPlay-capable source ...
AirPlay 2 fp-setup skipped or failed (FairPlay native library unavailable); proceeding without FairPlay.
```
