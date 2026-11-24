# MeetJoe desktop updates — always read before changing

**Gate:** Before you change packaging, installers, site download links, the desktop update API, Velopack wiring, deploy volumes, or `Directory.Build.props` `<Version>`, read this whole file and satisfy the checklist at the end.

Goal: users install **once** from the site (Velopack installer). After that, **Settings → Check for updates** applies newer builds in place. Never force uninstall → full redownload for a normal release.

---

## Hard rules

1. **Site downloads must be Velopack installers**, not plain DMG/app bundles that leave `UpdateManager.IsInstalled == false`.
   - Mac: `.pkg` from `vpk [osx] pack` (e.g. `MeetJoe-{version}-mac-arm64.pkg`)
   - Windows: Setup `.exe` from `vpk [win] pack` (e.g. `MeetJoe-{version}-Setup-win-x64.exe`)
2. **Do not ship a non-Velopack Mac DMG as the primary download** if you care about Check for updates. A DMG install cannot self-update.
3. **Every released version must appear on the live feed** for each RID you support:
   - Manifest: `https://api.meetjoe.joesolutions.ph/api/desktop/updates?rid={rid}`
   - Packages: `https://api.meetjoe.joesolutions.ph/updates/desktop/{rid}/`
4. **Bump semver** in `Directory.Build.props` (`<Version>`) for every published desktop release. Pack with that same version.
5. **Prod API must mount the feed.** Compose mounts `deploy/prod/desktop-releases` → `/app/releases/desktop`. Sync packed `releases/desktop/{rid}/` there; recreate/restart API if needed.
6. **Versioned filenames + cache bust** on the marketing site (`deploy/prod/site/index.html`) so browsers do not reuse an old installer under a same-named URL.
7. **Mac packaging must keep** `Info.plist`, `AppIcon.icns`, and `MeetJoe.entitlements` (camera/mic TCC) in the Velopack pack path — see `scripts/pack-desktop-update.sh`.

---

## Architecture (do not break)

| Piece | Role |
| --- | --- |
| `VelopackApp` in `MeetJoe.Desktop/Program.cs` | Marks the process as a Velopack app |
| `VelopackAppUpdateService` | Settings → Check for updates; uses feed from API |
| `GET /api/desktop/updates?rid=` | Public semver manifest (`DesktopUpdateFeed`) |
| Static `/updates/desktop/{rid}/` | nupkgs + `releases.*.json` |
| `scripts/pack-desktop-update.sh` | `dotnet publish` + `vpk pack` → `src/MeetJoe.Api/releases/desktop/{rid}/` |
| Site `downloads/` | First-time Velopack installers only |

RIDs in use: `osx-arm64`, `win-x64`.

---

## Publish a new desktop version

```bash
# 1. Bump <Version> in Directory.Build.props (e.g. 0.1.4 → 0.1.5)

# 2. Pack feeds (both RIDs you ship)
./scripts/pack-desktop-update.sh 0.1.5 osx-arm64
./scripts/pack-desktop-update.sh 0.1.5 win-x64

# 3. Copy Velopack installers into site downloads (versioned names)
#    From the pack output / artifacts — Mac .pkg and Windows Setup.exe
#    Update deploy/prod/site/index.html button hrefs + labels to the new version

# 4. Deploy to prod host
#    - Sync src/MeetJoe.Api/releases/desktop/ → /opt/meetjoe/deploy/prod/desktop-releases/
#    - Sync site downloads + index.html
#    - Ensure API container still mounts desktop-releases at /app/releases/desktop
#    - Restart/recreate API if the volume or files were empty before

# 5. Verify (must pass before telling the user to update)
curl -sS "https://api.meetjoe.joesolutions.ph/api/desktop/updates?rid=osx-arm64"
curl -sS "https://api.meetjoe.joesolutions.ph/api/desktop/updates?rid=win-x64"
# Expect JSON with the new Version for each.
```

Dev / `dotnet run` builds are **not** Velopack-installed; Settings should say auto-update needs a packaged install — that is expected.

---

## First install vs later updates

| User state | What they need |
| --- | --- |
| Never installed / DMG-only install | One download of the **Velopack** `.pkg` / Setup.exe from meetjoe.joesolutions.ph |
| Already on a Velopack install | Settings → **Check for updates** (or startup check). No uninstall. |

If Check for updates says the app is not installed / unsupported, the binary was not produced by `vpk pack`. Fix the download artifact — do not tell users to “just redownload the DMG” as the long-term path.

---

## Config (API)

```json
"DesktopUpdates": {
  "RootPath": "releases/desktop",
  "UrlPath": "/updates/desktop",
  "Channel": "stable"
}
```

Prod path on disk: `deploy/prod/desktop-releases/{rid}/` (compose volume).

---

## Related scripts (know the difference)

| Script | Use for |
| --- | --- |
| `scripts/pack-desktop-update.sh` | **Canonical** Velopack feed + installer pack. Prefer this for releases. |
| `deploy/prod/package-desktop.sh` | Legacy/local DMG + NSIS paths. Do **not** replace Velopack site downloads with its Mac DMG if you want Check for updates. |

---

## Pre-change checklist (required)

Before merging or deploying anything that touches desktop distribution:

- [ ] Read this file
- [ ] Version bumped in `Directory.Build.props` if shipping a new build
- [ ] `pack-desktop-update.sh` run for every shipped RID
- [ ] Live `GET /api/desktop/updates?rid=…` returns the new version
- [ ] Site buttons point at Velopack `.pkg` / Setup.exe with the new version in the filename
- [ ] Prod `desktop-releases` volume populated; API can serve `/updates/desktop/{rid}/`
- [ ] Mac pack still includes icon + entitlements (TCC / Dock icon)
- [ ] You did **not** make plain DMG the primary Mac download

If any box fails, stop — do not tell users to uninstall and redownload as the normal update path.
