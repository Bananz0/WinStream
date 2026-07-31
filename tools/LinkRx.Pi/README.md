# WinStream LinkRx.Pi

Linux / Raspberry Pi companion for **WinStream Link** (Track B). Speaks the same `WSL1` UDP media + TCP PIN control plane as `tools/LinkRx`.

## Requirements

- .NET 10 runtime (or publish self-contained)
- `alsa-utils` (`aplay`)
- Prefer direct ALSA device (`hw:0,0` or `plughw:0,0`) — avoid routing through Pulse/PipeWire for the lowest path

## Run

```bash
tools/LinkRx.Pi/run-linkrx.sh 1234 plughw:0,0 "Living room"
```

The script is a thin wrapper; the equivalent direct invocation is:

```bash
dotnet run --project tools/LinkRx.Pi/LinkRx.Pi.csproj -c Release -- \
  --pin 1234 --device plughw:0,0 --name "Living room"
```

| Option | Default | Purpose |
| --- | --- | --- |
| `--pin` | required | Shared PIN the Windows sender must present |
| `--device` | `plughw:0,0` | ALSA playout device |
| `--name` | hostname | Label shown in the Windows **Scan** list |
| `--no-advertise` | off | Stay off mDNS; connect by IP only |

Then from Windows (TX):

1. Enable `LinkFeatureEnabled` in `%LocalAppData%\WinStream\settings.json` (or future UI).
2. Sink mode → WinStream Link companion.
3. **Scan** and pick the Pi (or type its IP), enter the PIN, then Connect Link.

Or lab tone:

```powershell
dotnet run --project tools/LinkTx/LinkTx.csproj -c Release -- <pi-ip>
```

(PIN handshake is required when connecting from the WinStream app; LinkTx tone harness skips control for now.)

## Notes

- Media UDP default **47200**, control TCP **47201**
- Manual soak: connect → play 30s → confirm audible output + `rx packets=` logs
