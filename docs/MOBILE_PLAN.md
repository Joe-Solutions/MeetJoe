# MeetJoe Mobile (iOS + Android)

**Status:** Approved for execution (M0 scaffolding started).  
**Planning doc:** local; ship code on feature branches + PRs.  
**Desktop note:** YouTube Music (0.1.38) already on `main` via PR #59.

## Tech stack

| Layer | Choice |
|---|---|
| UI / shared | Avalonia 11 + `MeetJoe.App` |
| Hosts | `MeetJoe.iOS`, `MeetJoe.Android` |
| Backend | Existing MeetJoe API (REST + SignalR) + LiveKit |
| Auth | OpenIddict/OIDC via system browser |
| Push | APNs + FCM via `POST /api/devices` |
| Media | LiveKit page in platform WebView |
| Store updates | App Store / Play (not Velopack) |

## Goals (v1 subset)

**In:** Auth, Activity, Chat, Workspaces, Calendar/join, Calls, push, LiveKit A/V.  
**Deferred:** Enterprise admin, deep Files, Music keep-alive PiP, screen-share send, PSTN, webinars.

## Architecture

Thin platform hosts reference shared `MeetJoe.App`. Same contracts and API as desktop. Bundle / application id: `com.joesolutions.meetjoe`.

Adaptive shell: bottom tabs (Activity | Chat | Calendar | Calls | More); Chat is list → thread; meetings/calls full-screen.

## Store deployment

| Platform | Build | Artifact | Console |
|---|---|---|---|
| iOS | `dotnet publish` (Mac + Xcode) | `.ipa` | App Store Connect → TestFlight → App Store |
| Android | `dotnet publish` | `.aab` | Play Console → internal → production |

CI default: GitHub Actions + Fastlane; secrets in GitHub Environments.

## Phases

### M0 — Spike
- Scaffold iOS/Android hosts (**Android builds**; iOS needs Xcode + `ValidateXcodeVersion=false` if SDK/Xcode minor mismatch)
- Login + Chat list + SignalR on device/simulator
- LiveKit WebView + mic/camera permission
- Workloads: `dotnet workload install android ios`

### M1 — Messaging MVP
- Adaptive shell; Chat/Activity/Workspaces
- Push registration + mentions/DMs
- TestFlight + Play internal

### M2 — Meetings + Calls
- Join meetings; start/answer calls; calendar list
- Incoming call UI; CallKit if ringing needs it

### M3 — Store polish
- Icons, splash, privacy forms, Data safety
- Mobile CI jobs; release tracks

## Risks

- Avalonia mobile WebView/media maturity → prove in M0
- iOS background call limits → CallKit/PushKit in M2 if needed
- Desktop-wide UI → adaptive shell first

## Out of scope

- Expo / React Native / Flutter rewrite
- Full desktop layout parity on day one
