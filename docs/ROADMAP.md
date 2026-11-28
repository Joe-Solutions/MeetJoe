# MeetJoe Roadmap

Full product roadmap cloning Microsoft Teams core features into MeetJoe. Start at Phase 0; first shippable product is Phase 1 (balanced MVP).

## Feature map (Teams → MeetJoe)

| Teams capability | MeetJoe module | Notes |
| --- | --- | --- |
| Activity feed | Activity | Mentions, replies, reactions, missed calls, meeting starts |
| Chat (1:1, group) | Chat | Text, rich compose, threads, reactions, files, emoji, typing, read receipts |
| Teams + channels | Workspaces | Standard / private / shared channels, posts, tabs, channel files |
| Meetings | Meetings | Schedule/join, A/V, screen share, lobby, in-meeting chat, recording (later) |
| Calls / VoIP | Calls | 1:1 and group VoIP, call history; PSTN deferred |
| Calendar | Calendar | Schedule meetings, availability; external calendar sync later |
| Files | Files | Chat/channel uploads, preview, versioning via object storage |
| Presence / status | Presence | Online / Away / Busy / DND, custom status |
| Apps / tabs / bots | Extensions | Phase 6; web tabs + incoming webhooks first |
| Admin / policies | Admin | Orgs, roles, retention, guest access |
| Events / webinars | Events | Large-scale broadcast; late phase |
| AI assistants | Assist | Summaries, suggested replies; after core reliability |

### Explicitly deferred (not Phase 1–4)

PSTN / Teams Phone, Teams Rooms hardware, PowerPoint Live parity, Loop/Office deep integration, full app marketplace, eCDN.

---

## Phase 0 — Foundation

- [x] Create solution, Avalonia desktop host, API host
- [x] Docker Compose (Postgres, Redis, MinIO; LiveKit; coturn deferred)
- [x] Design tokens + Avalonia control styles; shell chrome with empty panes
- [x] Identity + login/register + org create/join
- [x] CI build for `MeetJoe.Desktop` + `MeetJoe.Api` (`.github/workflows/ci.yml`)

## Phase 1 — Balanced MVP

- [x] 1:1 and group **Chat** (SignalR send/receive, REST history, MinIO attachments)
- [x] **Workspaces + standard channels** with channel messaging
- [x] **Presence** (online/offline/away/busy) + basic profile/settings
- [x] **Basic meetings:** create/join + LiveKit room JWT; camera/mic/screen share via MeetJoe media page (`/meet/`, LiveKit JS); Avalonia stage lists participants and opens media
- [x] **Files:** upload/download from chat and channel; simple Files listing
- [x] Desktop debug builds for Mac + Windows

### Phase 0–1 build order

1. Scaffold solution + Docker Compose + design tokens
2. Auth + shell navigation + branded login
3. Persist orgs/users; wire JWT to Avalonia `HttpClient` + SignalR
4. Chat end-to-end, then Workspaces/Channels
5. LiveKit meeting stage, then file upload pipeline
6. Presence + settings; MVP demo (login → chat → channel → meet → share file)

### MVP success criteria

- Two users on Mac and Windows can DM, create a workspace/channel, exchange files, and join a live video meeting with mute/camera controls
- UI reads as one MeetJoe product (brand-first auth, teal/navy shell, three-pane layout)
- Structure allows iOS/Android hosts without rewriting ViewModels

**Today:** Phase 1 MVP works end-to-end including LiveKit A/V via the `/meet/` media page. Embedding WebRTC inside the Avalonia window (vs browser) is a later polish item.

## Phase 2 — Collaboration depth

- [x] Activity feed, @mentions, reactions, reply threads
- [x] Message edit/delete + link preview stub (hostname chip; OG fetch deferred)
- [x] Private channels, channel roles (owner/member)
- [x] Search (messages + people)
- [x] Typing indicators, read receipts, notification preferences, mute/pin chats
- [x] Rich compose: markdown subset, emoji picker, code blocks

## Phase 3 — Meetings v2 + Calendar

- [x] Schedule meetings, calendar day navigation + upcoming list, invites
- [x] Lobby, in-meeting chat, hand raise _(screen share remains on LiveKit media page)_
- [x] Host controls (mute/remove + lobby admit/reject)
- [x] Recordings stub (`RecordingStatus.Unavailable`; LiveKit egress → MinIO deferred)
- [x] Recurring meetings (series expansion of next ~8 instances)

## Phase 4 — Calls

- [x] 1:1 and group VoIP from chat (LiveKit room + `/meet/` media page)
- [x] Call history, missed-call activity items
- [ ] Transfer/hold deferred; no PSTN yet

## Phase 5 — Enterprise admin

- [ ] Org admin console (users, roles, guest access, retention)
- [ ] Device/session management, audit log
- [ ] SSO (OIDC enterprise IdP)
- [ ] Compliance export basics

## Phase 6 — Extensibility

- [ ] Channel/chat **tabs** (embedded web views)
- [ ] Incoming webhooks + simple bot framework
- [ ] App catalog stub (Tasks, Approvals)

## Phase 7 — Mobile

- [x] `MeetJoe.iOS` / `MeetJoe.Android` hosts sharing `MeetJoe.App` (M0 scaffold)
- [ ] Adaptive layouts (rail → bottom tab bar)
- [ ] Push notifications
- [ ] Mobile A/V via LiveKit
- [ ] Subset parity: Chat, Workspaces, Meetings, Activity, Calls

## Phase 8 — Scale & advanced

- [ ] Events/webinars (large rooms)
- [ ] AI meeting notes / suggested replies
- [ ] Horizontal scale (Redis SignalR backplane, LiveKit multi-node)
- [ ] Optional Linux desktop; web client only if needed

---

## Out of scope for initial execution

Do not block MVP on PSTN, marketplace, or AI. Media edge cases and admin depth can iterate after Phase 1.
