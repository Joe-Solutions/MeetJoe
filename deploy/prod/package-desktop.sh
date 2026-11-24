#!/usr/bin/env bash
# Build installable desktop packages from Release publish output.
#   ./deploy/prod/package-desktop.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PUBLISH="$ROOT/artifacts/publish"
OUT="$ROOT/artifacts/downloads"
STAGE="$ROOT/artifacts/package-stage"
VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
VERSION="${VERSION:-0.0.0}"

export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

mkdir -p "$OUT" "$STAGE"

echo "==> Publish osx-arm64 (v${VERSION})"
dotnet publish "$ROOT/src/MeetJoe.Desktop/MeetJoe.Desktop.csproj" \
  -c Release -r osx-arm64 --self-contained true \
  -o "$PUBLISH/osx-arm64"

echo "==> Assemble MeetJoe.app"
APP="$STAGE/MeetJoe.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# Entire self-contained publish lives next to the executable (Avalonia/.NET expectation).
rsync -a --delete "$PUBLISH/osx-arm64/" "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/MeetJoe"

# Canonical Info.plist + icon (TCC prompts require usage strings + a real .app bundle).
cp "$ROOT/src/MeetJoe.Desktop/Info.plist" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString ${VERSION}" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion ${VERSION}" "$APP/Contents/Info.plist"
ICON_SRC="$ROOT/src/MeetJoe.Desktop/Assets/AppIcon.icns"
if [[ ! -f "$ICON_SRC" ]]; then
  echo "Missing AppIcon.icns at $ICON_SRC" >&2
  exit 1
fi
cp "$ICON_SRC" "$APP/Contents/Resources/AppIcon.icns"

ENTITLEMENTS="$ROOT/src/MeetJoe.Desktop/MeetJoe.entitlements"
# Ad-hoc sign with hardened runtime + entitlements so camera/mic TCC dialogs can appear.
# Not notarized — first open may still need right-click → Open.
codesign --force --deep --options runtime \
  --entitlements "$ENTITLEMENTS" \
  --sign - "$APP"

echo "==> Build DMG"
DMG_STAGE="$STAGE/dmg"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP" "$DMG_STAGE/MeetJoe.app"
ln -sf /Applications "$DMG_STAGE/Applications"
DMG_OUT="$OUT/MeetJoe-mac-arm64.dmg"
rm -f "$DMG_OUT" "$OUT/MeetJoe-${VERSION}-mac-arm64.dmg"
hdiutil create -volname "MeetJoe" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG_OUT" >/dev/null
cp -f "$DMG_OUT" "$OUT/MeetJoe-${VERSION}-mac-arm64.dmg"
echo "Wrote $DMG_OUT and MeetJoe-${VERSION}-mac-arm64.dmg ($(du -h "$DMG_OUT" | cut -f1))"

echo "==> Publish win-x64"
dotnet publish "$ROOT/src/MeetJoe.Desktop/MeetJoe.Desktop.csproj" \
  -c Release -r win-x64 --self-contained true \
  -o "$PUBLISH/win-x64"

echo "==> Windows Setup.exe (NSIS)"
if ! command -v makensis >/dev/null 2>&1; then
  echo "makensis not found — install with: brew install nsis" >&2
  exit 1
fi
WIN_NSIS="$STAGE/nsis"
rm -rf "$WIN_NSIS"
mkdir -p "$WIN_NSIS/win-payload"
rsync -a --delete "$PUBLISH/win-x64/" "$WIN_NSIS/win-payload/"
# Inject current semver into the NSIS script.
sed "s/^!define APP_VERSION .*/!define APP_VERSION \"${VERSION}\"/" \
  "$ROOT/deploy/prod/meetjoe-win.nsi" > "$WIN_NSIS/meetjoe-win.nsi"
(
  cd "$WIN_NSIS"
  makensis -V2 meetjoe-win.nsi
)
WIN_OUT="$OUT/MeetJoe-Setup-win-x64.exe"
rm -f "$WIN_OUT" "$OUT/MeetJoe-${VERSION}-Setup-win-x64.exe"
mv "$WIN_NSIS/MeetJoe-Setup-win-x64.exe" "$WIN_OUT"
cp -f "$WIN_OUT" "$OUT/MeetJoe-${VERSION}-Setup-win-x64.exe"
echo "Wrote $WIN_OUT and MeetJoe-${VERSION}-Setup-win-x64.exe ($(du -h "$WIN_OUT" | cut -f1))"

echo "Done."
ls -lh "$OUT"/MeetJoe-mac-arm64.dmg "$OUT"/MeetJoe-Setup-win-x64.exe \
  "$OUT"/MeetJoe-${VERSION}-mac-arm64.dmg "$OUT"/MeetJoe-${VERSION}-Setup-win-x64.exe
