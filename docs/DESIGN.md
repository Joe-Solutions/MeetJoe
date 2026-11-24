# MeetJoe Design System

Elegant, modern UI that mirrors Microsoft Teams’ **productivity shell**, not its visual skin.

## Shell layout (desktop)

1. **Left rail** — Activity, Chat, Workspaces, Calendar, Calls, Files, Settings (icons + optional labels)
2. **List pane** — conversations, channels, or calendar agenda
3. **Main stage** — message thread, channel posts, meeting stage, or file browser
4. **Right contextual panel** (toggle) — members, files, search, meeting details

```
┌──────┬─────────────┬──────────────────────────┬────────────┐
│ Rail │ List pane   │ Main stage               │ Context    │
│ 80px │ 300px       │ *                        │ 280px      │
└──────┴─────────────┴──────────────────────────┴────────────┘
```

Rail buttons are **68×54**. Selection is owned by the view model (`Button.row` / `Button.rail` + `IsSelected`), not `ListBox`.

## Brand & look

| Token | Value | Use |
| --- | --- | --- |
| Canvas | `#0B1F33` | App background |
| Panel | `#122A40` | Elevated surfaces |
| Border | `#1E3A52` | Hairline separators |
| Text primary | `#E8F1F5` | Body / titles |
| Text muted | `#8AA0B2` | Secondary labels |
| Accent | `#1EC8C8` | CTA, active rail, focus |
| Danger | `#E85D4C` | Destructive / errors |

### Derived surfaces (in `Styles/Tokens.axaml`)

| Token | Value | Use |
| --- | --- | --- |
| Rail | `#081828` | Left navigation strip |
| Field | `#0E2538` | Inputs |
| PanelHover / PanelRaised | `#163149` / `#1A3852` | Row hover / raised panels |
| AccentHover / AccentPressed | `#3AD6D6` / `#17A9A9` | Primary button states |
| AccentSoft / OnAccent | `#291EC8C8` / `#04262B` | Soft accent wash / text on accent |
| DangerSoft | `#26E85D4C` | Soft error wash |

**Avoid:** purple-on-white / indigo gradients, warm cream + terracotta serif, broadsheet dense columns, heavy card grids, glow stacks, emoji clutter.

### Typography

- UI sans: **Manrope** (embedded under `Assets/Fonts`)
- Brand wordmark **MeetJoe** is the strongest signal on auth/splash — headlines must not overpower it

### Chrome rules

- Soft tonal layers + 1px separators instead of card grids
- Rounded corners only on interactive controls (inputs, buttons, avatars) — message rows stay flat
- No cards in the hero / auth viewport
- One job per section
- Auth backdrop: mesh radial gradients + tiled film-grain overlay (stops banding)

### Motion (shipped)

1. Pane slide on rail navigation — `SlideFadeTransition` (~180ms)
2. Message insert fade/rise — `Grid.message` style animation
3. Meeting join stage — `Grid.meetingstage.live` scale/fade

### Auth / first viewport

Full-bleed subtle mesh gradient behind:

- Centered **MeetJoe** wordmark
- One short product line
- Sign-in CTA group only

No stats, feature grids, or secondary promos in the first viewport.

## Key views (`MeetJoe.App`)

| View | Role |
| --- | --- |
| `BrandBackdrop` | Mesh + grain behind auth / org setup |
| `LoginView` | Brand-first sign-in / register |
| `OrganizationSetupView` | Create or join an organization |
| `ShellView` | Rail + section host + connection / meeting banners |
| `ChatView` | Conversation list + thread stage |
| `MessageThreadView` | Shared chat / channel message list + composer |
| `WorkspaceView` | Workspace / channel list + channel thread |
| `MeetingStageView` | Roster + leave/end; opens `/meet/` media page for LiveKit A/V |
| `CalendarView` | Agenda + create / join meetings |
| `FilesView` | Org / chat / channel files |
| `SettingsView` | Profile, presence, join code, sign out |
| `PlaceholderView` | Activity and Calls until those modules ship |

## Avalonia resources

Tokens live in `src/MeetJoe.App/Styles/Tokens.axaml`; control themes in `Styles/Controls.axaml`; Feather-derived geometries in `Styles/Icons.axaml`. FluentTheme Dark is recolored with `ColorPaletteResources` (Accent `#1EC8C8`). Prefer `Button` classes `primary` / `secondary` / `ghost` / `tint` / `row` / `rail` — do **not** reuse Fluent’s `accent` class name.
