# node_airtunes2 Sender Backend

WinStream now has an alternate sender backend that streams captured PCM into
`node_airtunes2` instead of using WinStream's own RTSP/RTP media transport.

Use it when:

- the receiver is an Apple TV / HomePod class AirPlay target
- FairPlay sender support is still missing from `winstream-playfair.dll`
- you want a practical sender path that already works in public code

## Local setup

The workspace has already been prepared with a repo-local sender checkout at:

`third_party/node_airtunes2`

The WinStream output now includes the bridge script:

`Node/airtunes-bridge.js`

## Enable it

Set:

`WINSTREAM_USE_NODE_AIRTUNES2=1`

Optional overrides:

- `WINSTREAM_NODE_PATH` to point at a specific `node` executable
- `WINSTREAM_NODE_AIRTUNES2_DIR` to point at a different `node_airtunes2` checkout
- `WINSTREAM_AIRPLAY_PIN` if the target requires PIN pairing

When enabled, `DeviceConnection` bypasses WinStream's RTSP media path and
creates an `AudioSessionManager` backed by `node_airtunes2`.

## Expected behavior

- WinStream still captures system audio the same way as before.
- Captured 44.1 kHz stereo PCM is streamed into the child Node process.
- The child process performs receiver handshake, pairing, ALAC packaging, and
  audio delivery.
- If the receiver does not become ready within 20 seconds, startup fails fast
  instead of hanging indefinitely.
