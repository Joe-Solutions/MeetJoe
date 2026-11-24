#!/usr/bin/env bash
# Build installable desktop packages from Release publish output.
#   ./deploy/prod/package-desktop.sh
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PUBLISH="$ROOT/artifacts/publish"
OUT="$ROOT/artifacts/downloads"
STAGE="$ROOT/artifacts/package-stage"

export PATH="${HOME}/.dotnet:${PATH}"
export DOTNET_ROOT="${HOME}/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

mkdir -p "$OUT" "$STAGE"

echo "==> Publish osx-arm64"
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

cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleDevelopmentRegion</key>
	<string>en</string>
	<key>CFBundleExecutable</key>
	<string>MeetJoe</string>
	<key>CFBundleIdentifier</key>
	<string>com.joesolutions.meetjoe</string>
	<key>CFBundleInfoDictionaryVersion</key>
	<string>6.0</string>
	<key>CFBundleName</key>
	<string>MeetJoe</string>
	<key>CFBundleDisplayName</key>
	<string>MeetJoe</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>0.1.1</string>
	<key>CFBundleVersion</key>
	<string>0.1.1</string>
	<key>LSMinimumSystemVersion</key>
	<string>12.0</string>
	<key>NSHighResolutionCapable</key>
	<true/>
	<key>NSCameraUsageDescription</key>
	<string>MeetJoe uses the camera for video calls and meetings.</string>
	<key>NSMicrophoneUsageDescription</key>
	<string>MeetJoe uses the microphone so you can talk in calls and meetings.</string>
</dict>
</plist>
PLIST

# Ad-hoc sign so macOS will launch the .app (not notarized — first open may need right-click → Open).
codesign --force --deep --sign - "$APP" 2>/dev/null || true

echo "==> Build DMG"
DMG_STAGE="$STAGE/dmg"
rm -rf "$DMG_STAGE"
mkdir -p "$DMG_STAGE"
cp -R "$APP" "$DMG_STAGE/MeetJoe.app"
ln -sf /Applications "$DMG_STAGE/Applications"
DMG_OUT="$OUT/MeetJoe-mac-arm64.dmg"
rm -f "$DMG_OUT"
hdiutil create -volname "MeetJoe" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG_OUT" >/dev/null
echo "Wrote $DMG_OUT ($(du -h "$DMG_OUT" | cut -f1))"

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
cp "$ROOT/deploy/prod/meetjoe-win.nsi" "$WIN_NSIS/meetjoe-win.nsi"
(
  cd "$WIN_NSIS"
  makensis -V2 meetjoe-win.nsi
)
WIN_OUT="$OUT/MeetJoe-Setup-win-x64.exe"
rm -f "$WIN_OUT"
mv "$WIN_NSIS/MeetJoe-Setup-win-x64.exe" "$WIN_OUT"
echo "Wrote $WIN_OUT ($(du -h "$WIN_OUT" | cut -f1))"

echo "Done."
ls -lh "$OUT"/MeetJoe-mac-arm64.dmg "$OUT"/MeetJoe-Setup-win-x64.exe
