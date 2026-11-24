#!/usr/bin/env bash
# Build a Velopack release (semver) and publish it to the API update feed.
# Usage:
#   ./scripts/pack-desktop-update.sh [version] [rid]
# Example:
#   ./scripts/pack-desktop-update.sh 0.1.3 osx-arm64
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
if [[ -x "$ROOT/.tools/vpk" ]]; then
  VPK=("$ROOT/.tools/vpk")
else
  dotnet tool install vpk --version 1.2.161 --tool-path "$ROOT/.tools" >/dev/null
  VPK=("$ROOT/.tools/vpk")
fi

PUBLISH="$ROOT/artifacts/publish/$RID"
RELEASES="$ROOT/src/MeetJoe.Api/releases/desktop/$RID"
# Publish into an empty dir: `dotnet publish -o` keeps stale files from earlier builds
# (e.g. net9 SignalR DLLs after the net8 retarget → startup crash on System.Runtime 9).
rm -rf "$PUBLISH"
mkdir -p "$PUBLISH" "$RELEASES"

echo "==> Publishing MeetJoe $VERSION ($RID)"
dotnet publish "$ROOT/src/MeetJoe.Desktop/MeetJoe.Desktop.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:Version="$VERSION" \
  -p:InformationalVersion="$VERSION" \
  -o "$PUBLISH"

PACK_EXTRA=()
ICON_ARGS=()
MAIN_EXE=MeetJoe
# Ensure empty arrays are safe under `set -u`.
if [[ "$RID" == osx-* ]]; then
  ICON="$ROOT/src/MeetJoe.Desktop/Assets/AppIcon.icns"
  PLIST="$ROOT/src/MeetJoe.Desktop/Info.plist"
  ENTITLEMENTS="$ROOT/src/MeetJoe.Desktop/MeetJoe.entitlements"
  cp -f "$PLIST" "$PUBLISH/Info.plist"
  cp -f "$ICON" "$PUBLISH/AppIcon.icns"
  /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${VERSION}" "$PUBLISH/Info.plist" 2>/dev/null || true
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${VERSION}" "$PUBLISH/Info.plist" 2>/dev/null || true
  PACK_EXTRA+=(--plist "$PUBLISH/Info.plist")
  # vpk skips codesign entirely without an identity, shipping the linker's "apphost"
  # signature with Info.plist unbound — TCC then never lists MeetJoe for mic/camera.
  # Ad-hoc ("-") by default; set MEETJOE_MAC_SIGN_IDENTITY to a Developer ID to keep
  # TCC grants across updates.
  PACK_EXTRA+=(--signAppIdentity "${MEETJOE_MAC_SIGN_IDENTITY:--}")
  if [[ -z "${MEETJOE_MAC_SIGN_IDENTITY:-}" ]]; then
    # An ad-hoc designated requirement is the cdhash, so TCC treats every update as a new
    # app and drops its mic/camera grant. vpk calls `codesign` from PATH and has no flag
    # for extra args, so a shim pins the requirement to the bundle id instead.
    SHIM_DIR="$(mktemp -d)"
    trap 'rm -rf "$SHIM_DIR"' EXIT
    cat > "$SHIM_DIR/codesign" <<'SHIM'
#!/usr/bin/env bash
exec /usr/bin/codesign '-r=designated => identifier "com.joesolutions.meetjoe"' "$@"
SHIM
    chmod +x "$SHIM_DIR/codesign"
    export PATH="$SHIM_DIR:$PATH"
  fi
  if [[ -f "$ENTITLEMENTS" ]]; then
    PACK_EXTRA+=(--signEntitlements "$ENTITLEMENTS")
  fi
elif [[ "$RID" == win-* ]]; then
  MAIN_EXE=MeetJoe.exe
  ICON="$ROOT/src/MeetJoe.Desktop/Assets/MeetJoe.ico"
else
  ICON="$ROOT/src/MeetJoe.Desktop/Assets/MeetJoe.png"
fi
if [[ -n "${ICON}" && -f "$ICON" ]]; then
  ICON_ARGS=(--icon "$ICON")
fi

# Velopack OS directive for cross-compiling, e.g. `vpk [win] pack` from macOS.
VPK_OS_DIR=
case "$RID" in
  win-*) VPK_OS_DIR='[win]' ;;
  osx-*) VPK_OS_DIR='[osx]' ;;
  linux-*) VPK_OS_DIR='[linux]' ;;
esac

echo "==> Packing Velopack release (channel=stable, os=${VPK_OS_DIR:-host})"
if [[ -n "$VPK_OS_DIR" ]]; then
  set -- "${VPK[@]}" "$VPK_OS_DIR" pack
else
  set -- "${VPK[@]}" pack
fi
"$@" \
  --packId MeetJoe \
  --packVersion "$VERSION" \
  --packDir "$PUBLISH" \
  --mainExe "$MAIN_EXE" \
  --packTitle MeetJoe \
  --packAuthors "Joe Solutions" \
  --outputDir "$RELEASES" \
  --channel stable \
  --runtime "$RID" \
  ${ICON_ARGS[@]+"${ICON_ARGS[@]}"} \
  ${PACK_EXTRA[@]+"${PACK_EXTRA[@]}"}
cat > "$RELEASES/RELEASE_NOTES.md" <<EOF
MeetJoe $VERSION
EOF

echo "==> Feed ready at $RELEASES"
ls -lh "$RELEASES" | head -30
echo "    Clients will pull: {API}/updates/desktop/$RID/"
echo "    Manifest:          {API}/api/desktop/updates?rid=$RID"
