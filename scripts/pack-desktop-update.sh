#!/usr/bin/env bash
# Build a Velopack release (semver) and publish it to the API update feed.
# Usage:
#   ./scripts/pack-desktop-update.sh [version] [rid]
# Example:
#   ./scripts/pack-desktop-update.sh 0.2.0 osx-arm64
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${1:-}"
RID="${2:-}"

if [[ -z "$VERSION" ]]; then
  VERSION="$(grep -E '<Version>' "$ROOT/Directory.Build.props" | sed -E 's/.*<Version>([^<]+)<\/Version>.*/\1/' | head -1)"
fi

if [[ -z "$RID" ]]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) RID=osx-arm64 ;;
    Darwin-*) RID=osx-x64 ;;
    Linux-aarch64) RID=linux-arm64 ;;
    Linux-*) RID=linux-x64 ;;
    MINGW*|MSYS*|CYGWIN*) RID=win-x64 ;;
    *) echo "Pass an RID (osx-arm64, win-x64, …)." >&2; exit 1 ;;
  esac
fi

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-].+)?$ ]]; then
  echo "Version must be semver (got '$VERSION')." >&2
  exit 1
fi

export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT

echo "==> Ensuring vpk tool"
dotnet tool restore >/dev/null
if ! dotnet tool run vpk --help >/dev/null 2>&1; then
  dotnet tool install vpk --version 1.2.161 --tool-path "$ROOT/.tools" >/dev/null
  export PATH="$ROOT/.tools:$PATH"
fi
VPK=(dotnet tool run vpk)
if [[ -x "$ROOT/.tools/vpk" ]]; then VPK=("$ROOT/.tools/vpk"); fi

PUBLISH="$ROOT/artifacts/publish/$RID"
RELEASES="$ROOT/src/MeetJoe.Api/releases/desktop/$RID"
mkdir -p "$PUBLISH" "$RELEASES"

echo "==> Publishing MeetJoe $VERSION ($RID)"
dotnet publish "$ROOT/src/MeetJoe.Desktop/MeetJoe.Desktop.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:InformationalVersion="$VERSION" \
  -o "$PUBLISH"

ICON_ARGS=()
if [[ "$RID" == osx-* ]]; then
  ICON="$ROOT/src/MeetJoe.Desktop/Assets/AppIcon.icns"
  # Ensure the .app Info.plist + icns land in the publish folder for Velopack.
  cp -f "$ROOT/src/MeetJoe.Desktop/Info.plist" "$PUBLISH/Info.plist"
  cp -f "$ICON" "$PUBLISH/AppIcon.icns"
elif [[ "$RID" == win-* ]]; then
  ICON="$ROOT/src/MeetJoe.Desktop/Assets/MeetJoe.ico"
else
  ICON="$ROOT/src/MeetJoe.Desktop/Assets/MeetJoe.png"
fi
if [[ -f "$ICON" ]]; then
  ICON_ARGS=(--icon "$ICON")
fi

echo "==> Packing Velopack release"
"${VPK[@]}" pack \
  --packId MeetJoe \
  --packVersion "$VERSION" \
  --packDir "$PUBLISH" \
  --mainExe MeetJoe \
  --outputDir "$RELEASES" \
  --channel stable \
  "${ICON_ARGS[@]}"

cat > "$RELEASES/RELEASE_NOTES.md" <<EOF
MeetJoe $VERSION
EOF

echo "==> Feed ready at $RELEASES"
echo "    Clients will pull: {API}/updates/desktop/$RID/"
echo "    Manifest:          {API}/api/desktop/updates?rid=$RID"
