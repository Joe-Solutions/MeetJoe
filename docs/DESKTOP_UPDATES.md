# Desktop auto-updates (semver + Velopack)

Installed MeetJoe Desktop builds check `{API}/api/desktop/updates?rid={rid}` on startup, then download **delta** (or full) packages from `{API}/updates/desktop/{rid}/` and restart into the new version. Users do not re-download a DMG/MSI for each release.

## Versioning

- App version is semver from `Directory.Build.props` (`<Version>0.1.0</Version>`).
- Bump **MAJOR.MINOR.PATCH** for each published desktop release.
- Dev/`dotnet run` builds are not Velopack-installed; Settings shows that auto-update needs a packaged install.

## Publish a release

```bash
# bump Version in Directory.Build.props first, then:
./scripts/pack-desktop-update.sh 0.2.0 osx-arm64
# Windows:
./scripts/pack-desktop-update.sh 0.2.0 win-x64
```

This publishes self-contained binaries, runs `vpk pack`, and writes packages under:

`src/MeetJoe.Api/releases/desktop/{rid}/`

Restart (or redeploy) the API so clients can fetch the feed.

## First install

Ship the Velopack installer / setup produced by `vpk pack` (or `vpk download`) to users once. After that, updates apply in place.

## Config

```json
"DesktopUpdates": {
  "RootPath": "releases/desktop",
  "UrlPath": "/updates/desktop",
  "Channel": "stable"
}
```
