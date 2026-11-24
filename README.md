# MeetJoe

Elegant, modern collaboration hub — a full-stack Microsoft Teams–parity product for **Mac** and **Windows**, with a path to **iOS** and **Android**.

Built with **C# Avalonia UI** (client) and **ASP.NET Core** (backend).

## Stack

| Layer | Choice |
| --- | --- |
| Desktop / future mobile UI | Avalonia 11.x + .NET 9, MVVM |
| API | ASP.NET Core modular monolith, REST + SignalR |
| Auth | ASP.NET Core Identity + OpenIddict (OIDC/JWT) |
| Media (A/V) | WebRTC via **LiveKit** SFU (+ coturn later) |
| Data | PostgreSQL, Redis, MinIO |

> **Media note:** Use WebRTC (via LiveKit). EasyRTC is an older Node wrapper on top of WebRTC and is not a fit for this stack.

## Docs

- [Roadmap](docs/ROADMAP.md) — phased feature plan (Teams parity)
- [Architecture](docs/ARCHITECTURE.md) — system design, solution layout, domain model
- [Design system](docs/DESIGN.md) — UI shell, brand, tokens, motion

## Platforms

1. **Now:** Mac + Windows (Avalonia Desktop)
2. **Later:** iOS + Android (shared `MeetJoe.App`, thin platform hosts)

## Status

**Phase 0 Foundation** and **Phase 1 Balanced MVP** are implemented locally:

- Auth, orgs, chat, workspaces/channels, files, presence, settings, calendar/meeting CRUD
- Meetings: LiveKit room tokens + browser **MeetJoe media** page (`/meet/`) for camera, mic, and screen share (Avalonia stage opens it on join)

## Run locally

Requires [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and Docker.

If the SDK lives in `~/.dotnet` (side-by-side install), put it on `PATH` for the shell session and set `DOTNET_ROOT` when launching the desktop apphost:

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
```

### 1. Infrastructure

```bash
docker compose -f deploy/docker-compose.yml up -d
```

| Service | Host |
| --- | --- |
| PostgreSQL | `localhost:5433` (avoids colliding with a local 5432) |
| Redis | `localhost:6379` |
| MinIO | `localhost:9000` (console `9001`) |
| LiveKit | `ws://localhost:7880` |

Credentials match `src/MeetJoe.Api/appsettings.Development.json` (dev only).

### 2. API

```bash
dotnet run --project src/MeetJoe.Api
# → http://localhost:5080  (GET /health)
```

### 3. Desktop

```bash
dotnet run --project src/MeetJoe.Desktop
# optional override:
# MEETJOE_API_URL=http://localhost:5080
```

Installed builds auto-update via **semver + Velopack** (delta packages from the API). See [docs/DESKTOP_UPDATES.md](docs/DESKTOP_UPDATES.md). Publish a release with:

```bash
./scripts/pack-desktop-update.sh 0.2.0 osx-arm64
```

### Tests

```bash
dotnet test MeetJoe.sln

# Optional: client against a running API + Docker stack
MEETJOE_LIVE_API=http://localhost:5080 dotnet test tests/MeetJoe.App.Tests

# Optional: point API integration tests at another Postgres
# MEETJOE_TEST_POSTGRES='Host=localhost;Port=5433;Username=meetjoe;Password=meetjoe_dev'
```

Headless UI screenshots land in `artifacts/screenshots/` (gitignored).

## MVP success criteria

Two users on Mac and Windows can DM, create a workspace/channel, exchange files, and join a live video meeting with mute/camera controls.

Joining a meeting opens the media page (mute / camera / share). Embedding that WebRTC surface inside Avalonia (vs the browser window) needs a licensed WebView or a native LiveKit client later.
