#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <PIN> [ALSA_DEVICE] [ADVERTISED_NAME]" >&2
  exit 2
fi

PIN="$1"
DEVICE="${2:-plughw:0,0}"
NAME="${3:-$(hostname)}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

exec dotnet run \
  --project "$SCRIPT_DIR/LinkRx.Pi.csproj" \
  -c Release \
  -- \
  --pin "$PIN" \
  --device "$DEVICE" \
  --name "$NAME"
