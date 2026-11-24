#!/usr/bin/env bash
# Launch MeetJoe as a proper .app so Info.plist camera/mic usage strings apply (TCC).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/src/MeetJoe.Desktop/bin/Debug/net9.0"
APP="$OUT/MeetJoe.app"
PLIST="$ROOT/src/MeetJoe.Desktop/Info.plist"

if [[ ! -x "$OUT/MeetJoe" ]]; then
  echo "MeetJoe binary not found at $OUT/MeetJoe — build Desktop first." >&2
  exit 1
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$PLIST" "$APP/Contents/Info.plist"
# Launcher keeps deps next to the real binary (output dir) while presenting a bundle id to macOS.
cat > "$APP/Contents/MacOS/MeetJoe" <<EOF
#!/bin/bash
cd "$OUT"
exec "$OUT/MeetJoe" "\$@"
EOF
chmod +x "$APP/Contents/MacOS/MeetJoe"
open "$APP"
