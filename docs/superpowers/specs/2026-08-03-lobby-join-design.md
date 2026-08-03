# Lobby Join & Reconnect (Launcher + Mod) — Design

**Date:** 2026-08-03
**Status:** Approved

## Overview

Host creates an Among Us lobby on a custom region -> mod detects it -> launcher uploads
lobby + mod set to self-hosted backend -> Discord bot posts `amonglauncher://join?code=...`
-> joiner clicks -> launcher sets up/downloads mods/launches -> mod joins the lobby.
Host mod changes -> backend pushes kick + rejoin to connected launchers automatically.

Hosts get a live control panel (code, player list, quick actions), a mod profile/preset
switcher, and live Discord embed updates (player count, join button, auto-cleanup).

Scope: **launcher + Among API mod only**. The backend and Discord bot are owned by the
user; this spec defines the contract they must implement.

## Components

| Component | Where | New responsibilities |
|-----------|-------|---------------------|
| Among API mod | `Among API\` | Lobby-create/close detection (host), live player join/left slot tracking, direct join handler (joiner), inbound IPC dispatch (currently missing), actually emit `game_ready`, optional host chat commands (`/repost`, `/disband`) |
| Launcher | `Among Launcher\` | `amonglauncher://` URI scheme + single-instance deep-link routing, `LobbyJoinService` (resolve -> install -> launch -> join), `LobbyCommandService` (WebSocket kick/rejoin), Host Live Control Panel UI, Mod Profile/Preset Switcher, mod-set diff vs installed plugins |
| Backend (user-owned) | external | Store lobby state (code -> mod set, host, connected users), REST endpoints (create/fetch/repost/kick/disband/heartbeat), WebSocket push (kick/rejoin), auto-expiry of crashed lobbies |
| Discord bot (user-owned) | external | Post + live-edit an embed (player count, join button, region), auto-cleanup on lobby end |

## Data Flow

### Host creates lobby
1. Host creates lobby in game (code `ALSKDJ`, custom region).
2. Mod's Harmony hook detects lobby creation -> sends IPC `lobby_created` to launcher: `{ code, region, regionIp }`.
3. Launcher reads installed mods (BepInEx/plugins DLLs + metadata) -> `POST /lobby` to backend: `{ hostUserId, code, region, modSet }`.
4. Backend stores lobby, asks bot to post invite `amonglauncher://join?code=ALSKDJ`.
5. If a previous lobby from this host had connected users and the mod set differs -> backend pushes `rejoin` to each connected launcher.
6. Launcher registers the lobby as active; backend starts a heartbeat expiry timer; bot embed shows `0/15 Players` (or the game's current count).

### Joiner clicks link
1. OS launches launcher with URI `amonglauncher://join?code=ALSKDJ` (scheme registered on install).
2. Launcher parses code, authenticates with Discord OAuth token -> `GET /lobby/{code}` -> `{ modSet, region }`.
3. Launcher does full setup if missing (copy game, BepInEx, AmongApi), ensures mods match `modSet`, launches game.
4. Mod connects pipe -> sends `game_ready`.
5. Launcher opens WebSocket to backend (identifies as this Discord user, declares "in lobby ALSKDJ").
6. Launcher sends IPC `join_lobby` to mod: `{ code, region, regionIp }`.
7. Mod sets region, calls `AmongUsClient.JoinOnlineGame(code)`.
8. On success, mod emits `join_lobby_result { success: true }`; backend increments the embed player count.

### Live player tracking (both host and joiners)
1. Mod watches player list changes in the lobby (`PlayerControl.AllPlayerControls` / lobby member events).
2. On join: mod emits `player_joined { code, playerName, playerCount }` -> launcher -> backend -> bot edits embed to `N/15 Players`.
3. On leave: mod emits `player_left { code, playerName, playerCount }` -> same path, embed decremented.
4. Host's launcher refreshes the Host Live Control Panel player list with Discord tags (map player name -> Discord user via backend membership).

### Host ends or disbands lobby
1. Host exits to menu or runs `/disband`: mod emits `lobby_closed { code, reason }` (or `DELETE /lobby/{code}` from launcher for a manual disband).
2. Launcher removes the lobby from its active list, tells backend.
3. Backend deletes lobby state, cancels heartbeat expiry, bot deletes/cleans up the embed.

### Host mod change (kick + reconnect)
1. Host changes mods in launcher (via Mod Profile/Preset Switcher), relaunches game, creates new lobby (steps 1-4 of host flow). Backend sees new mod set.
2. Backend pushes `rejoin` `{ lobbyCode, modSet }` via WebSocket to each previously-connected launcher.
3. Each launcher kills the running game, installs the new mod set (with file-lock-safe waits), relaunches, reconnects, rejoins — fully automatic.
4. "Kick specific player" = host clicks Kick in the control panel -> `POST /lobby/{code}/kick` -> backend closes that launcher's WebSocket / pushes `kick`; launcher kills the game.

## Host Utilities & Lobby Management

### Among API mod (in-game utility)
- **`lobby_closed` IPC emission:** on host exit to menu (`GameState` transition away from active lobby) or `Disconnect`, emit `lobby_closed { code, reason }`.
- **`player_joined` / `player_left` IPC emissions:** live slot tracking; emitted on lobby member changes, throttled (debounced ~500ms) to avoid storms.
- **Optional host chat commands** (in-game, prefix `/`):
  - `/repost` — re-emit `lobby_created`, launcher re-POSTs to backend, bot re-posts/re-focuses embed.
  - `/disband` — emit `lobby_closed { code, reason: "disband" }` then leave the lobby.

### Among Launcher (Host Live Control Panel)
New `Views/HostControlPanelView` bound to the active lobby state:
- **Code display:** large `ALSKDJ` + copy-to-clipboard.
- **Custom region indicator:** region name + server IP from the last `lobby_created`.
- **Active player list:** live, with Discord tags (resolved from backend membership by user id).
- **Quick actions:**
  - **Re-post to Discord** -> `POST /lobby/{code}/repost`.
  - **Kick Player** (per-row) -> `POST /lobby/{code}/kick` with `{ targetUserId }`.
  - **Disband Lobby** -> confirm modal -> `DELETE /lobby/{code}` -> kill game via existing `GameProcessManager`.

**Mod Profile/Preset Switcher:** host chooses a named profile (mod collection) before hosting.
- Profiles are stored in launcher config (`Profiles`) as `{ name, modSet }`.
- Switching a profile diffs against installed plugins and queues installs (respecting file locks),
  then relaunches the game — same mechanism as the joiner rejoin path.

### Self-hosted backend endpoints
| Method | Endpoint | Purpose |
|--------|----------|---------|
| POST | `/lobby` | Create/register lobby `{ hostUserId, code, region, modSet }` |
| GET | `/lobby/{code}` | Fetch lobby `{ code, region, modSet, playerCount, hostUserId }` |
| POST | `/lobby/{code}/repost` | Re-post/refresh the Discord embed |
| POST | `/lobby/{code}/kick` | Kick one player `{ targetUserId }` -> WS push `kick` |
| DELETE | `/lobby/{code}` | Disband/delete lobby -> bot cleans up embed |
| POST | `/lobby/{code}/heartbeat` | Keepalive from host launcher; missed heartbeats auto-expire the lobby |

Auto-expiry: if the host launcher stops sending heartbeats (e.g. crash/alt-F4), the backend
expires the lobby after a grace period, deletes state, and removes the embed.

### Discord bot (dynamic live embed)
- Single message per lobby, edited in place as state changes:
  - Title/link: `Join Lobby` button (URL `amonglauncher://join?code=...`).
  - Footer/field: `N/15 Players`, host name, custom region label.
- Updates: player count (from `player_joined`/`player_left`), repost, kick/status changes.
- Cleanup: on `DELETE /lobby/{code}` or lobby expiry, delete/expire the embed.

## IPC Protocol Additions

- Mod -> Launcher: `lobby_created { code, region, regionIp }`, `lobby_closed { code, reason? }`,
  `player_joined { code, playerName, playerCount }`, `player_left { code, playerName, playerCount }`,
  `join_lobby_result { success, error? }`
- Launcher -> Mod: `join_lobby { code, region, regionIp }`

Fixes required:
- The mod's `PipeClient.ListenAsync` only matches responses by `id`; it needs an inbound handler dispatch path to receive `join_lobby`.
- The mod must actually emit `game_ready` (documented in api.md but never implemented).

Region encoding: since lobbies run on custom servers, `join_lobby` carries region name +
server IP/port so the mod can call `ServerManager` to register/select it before joining.

## Mod In-Game Work

The mod currently has zero game interaction (no Among Us assembly references).

1. **Lobby-creation detection (host):** watch `AmongUsClient.Instance.GameState` (Harmony patch
   on the state setter or a coroutine/Update check). On host lobby creation, read the lobby code
   and emit `lobby_created` once; on transition away, emit `lobby_closed`.
2. **Live slot tracking:** on lobby membership change, debounce and emit `player_joined`/`player_left`.
3. **Direct join (joiner):** on `join_lobby`, register/select the custom region via `ServerManager`,
   decode the lobby code, call the game's join API, emit `join_lobby_result`.
4. **Host chat commands:** `/repost` and `/disband` hooks wired to the same IPC emissions.

### Research spike (gates mod-join coding)
- Exact `AmongUsClient.JoinOnlineGame` signature for the installed game version.
- **IL2CPP custom region injection:** how `ServerManager.Instance` / `HttpServerManager` expose region
  registration — which fields/methods must be populated (name, `Ip`, `Port`, ping ip) before `JoinOnlineGame`
  can target a custom region, and the correct order (register -> select -> join).
- Code -> int decoding (`GameCode` class).
- Reference strategy for the game's IL2CPP assemblies in the mod csproj and CI `build.yml`.

## Launcher Work

- **URI scheme registration:** `amonglauncher` -> launcher exe in registry, registered silently at
  startup if absent.
- **Single-instance deep-link routing:** instance #2 forwards `args[0]` to the primary running instance
  over a private NamedPipe (`AmongLauncher.Redirect`) before exiting; the primary parses the URI and
  starts the join flow. Fallback: if no pipe, the secondary instance becomes primary.
- **`LobbyJoinService`:** parse URI -> code; `GET /lobby/{code}`; full setup if missing; diff installed
  mods vs `modSet` and download missing via existing `DownloadModAsync` pipeline; launch game; wait
  for `game_ready`; send `join_lobby`.
- **`LobbyCommandService`:** WebSocket to backend, identify via Discord token, declare lobby membership;
  handle `kick` and `rejoin`; reconnect with backoff and re-declare on reconnect. Structured so the
  transport could be swapped for polling later.
- **`LobbyHeartbeatService`:** periodic `POST /lobby/{code}/heartbeat` while hosting.
- **Host Live Control Panel + Mod Profile Switcher:** see Host Utilities section.
- **Config:** add `BackendUrl`, `Profiles`.

## File Lock Handling (mod updates / hot rejoin)

Overwriting or deleting `.dll` files while Among Us is running fails with `IOException` (sharing violation).
- Before any install/overwrite/delete of a plugin DLL, if the game process is running, terminate it and
  wait with `process.WaitForExit()` (bounded, e.g. up to 15s) before touching files.
- Retry loop on file operations: up to 5 attempts with backoff (250ms, 500ms, 1s, 2s, 4s) on
  `IOException`/sharing violations.
- Apply the same discipline in the joiner rejoin path (kill -> wait -> install new mods -> relaunch).

## Error Handling

- Lobby fetch fails / code not found: launcher shows status message, does not launch game.
- Join fails (lobby full, wrong region, game error): mod sends `join_lobby_result { success: false, error }`;
  launcher surfaces it. Max 1 retry with brief delay.
- **Boot crash guard:** monitor the game process with early `Process.HasExited` checks alongside the
  90s `game_ready` timeout — a crashed/broken mod boot is detected immediately, not at timeout.
- WebSocket drops: reconnect with backoff; re-declare lobby membership on reconnect.
- Backend unreachable at lobby creation: host's mod retries `lobby_created` up to N times; if still down,
  the lobby simply isn't posted (game still works). Heartbeat still attempts in background.
- File locks during mod updates: handled via the File Lock Handling rules above.
- Heartbeat expiry: backend auto-clears stale lobbies; launcher re-registers if it reconnects.

## Testing

- IPC unit tests: `join_lobby` handler on the mod side (mocked game client), `lobby_created` /
  `lobby_closed` / `player_joined` / `player_left` emissions.
- Deep link: register scheme, launch with URI arg, verify single-instance NamedPipe routing.
- Join pipeline: with a real backend stub, verify mod-set diff + download + launch sequence.
- File lock safety: install/overwrite/delete DLL while game running -> verify kill+wait+retry path.
- Host control panel: repost, kick, disband against a backend stub; embed count updates.
- Heartbeat expiry: stop heartbeats -> backend expires lobby -> embed cleaned up.
- Manual end-to-end: host creates lobby -> invite posted -> joiner clicks -> joins; live count
  updates; mod-change -> kick -> reconnect; disband -> embed removed.
- Research spike deliverables verified before writing mod-join code.

## Open Decisions

- Backend request/response shapes: drafted in the implementation plan; adjust to match the user's backend.
- "Launcher closed but game running" kick gap: **accepted**. Kick only reaches launchers with the launcher
  running; the mod does not hold a second socket. Heartbeat expiry covers the host side of this gap.
- URI format: `amonglauncher://join?code=ALSKDJ`.
- Single-instance deep-link routing confirmed acceptable.
