#!/usr/bin/env bash
# Build and run the demo, then collect the diagnostic screenshots it writes.
#
#   usage:  bash run-demo.sh            build, run the automated measurement, then open the demo
#           bash run-demo.sh --no-gui   build and measure only (no interactive window)
#
#   Output goes to ./out/ :
#     run-<os>.log                     environment, build output, measured values
#     ime-preedit-diag-ruler1..7.png   screenshots of seven states
set -u

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJ="$SCRIPT_DIR/src/ImePreeditDemo/ImePreeditDemo.csproj"
OUT="$SCRIPT_DIR/out"
mkdir -p "$OUT"

case "$(uname -s)" in
  Linux)  OS=linux ;;
  Darwin) OS=mac ;;
  *)      OS=other ;;
esac

# The .NET SDK is often installed under ~/.dotnet rather than on PATH.
if [ -x "$HOME/.dotnet/dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  case ":$PATH:" in
    *":$HOME/.dotnet:"*) ;;
    *) PATH="$HOME/.dotnet:$PATH"; export PATH ;;
  esac
fi
if ! command -v dotnet >/dev/null 2>&1 && [ -x "/usr/local/share/dotnet/dotnet" ]; then
  PATH="/usr/local/share/dotnet:$PATH"; export PATH
fi

LOG="$OUT/run_${OS}.log"
: > "$LOG"
log() { printf '%s\n' "$*" | tee -a "$LOG"; }

log "=== AvaloniaEdit IME preedit demo - $(date '+%Y-%m-%d %H:%M:%S') ==="
log "os      : $(uname -s) $(uname -r)"
log "dotnet  : $(dotnet --version 2>&1)"
log "output  : $OUT"
log ""

if ! command -v dotnet >/dev/null 2>&1; then
  log "FAILED: dotnet not found. Install the .NET 10 SDK first."
  exit 1
fi

log "--- 1. build ---"
dotnet build "$PROJ" -c Debug 2>&1 | tee -a "$LOG"
if [ "${PIPESTATUS[0]}" -ne 0 ]; then
  log ""
  log "FAILED: build error, see $LOG"
  exit 1
fi

EXE="$SCRIPT_DIR/src/ImePreeditDemo/bin/Debug/net10.0/ImePreeditDemo"
if [ ! -x "$EXE" ] && [ -x "$EXE.exe" ]; then EXE="$EXE.exe"; fi
if [ ! -x "$EXE" ]; then
  log "FAILED: executable not found at $EXE"
  exit 1
fi

log ""
log "--- 2. automated measurement (--diag-ruler, exits by itself after ~15 s) ---"
"$EXE" --diag-ruler 2>&1 | tee -a "$LOG"

for d in "${TMPDIR:-/tmp}" "/tmp"; do
  for f in "$d"/ime-preedit-diag*.png; do
    [ -e "$f" ] && cp -f "$f" "$OUT/" 2>/dev/null
  done
done
log ""
log "log        : $LOG"
log "screenshots: $OUT"
log "what to look for: diff=0.000 on every line (the ruler marker and the caret agree)"
log ""

if [ "${1:-}" = "--no-gui" ]; then
  log "=== done (skipping the interactive run) ==="
  exit 0
fi

log "--- 3. interactive demo (close the window when finished) ---"
log "    Type Japanese, Chinese or Korean into the editor and watch the composition text."
"$EXE"
log "=== done ($(date '+%H:%M:%S')) ==="
