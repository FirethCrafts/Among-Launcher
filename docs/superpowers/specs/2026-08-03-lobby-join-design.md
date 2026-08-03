# Lobby Join & Reconnect (Launcher + Mod) — Design

**Date:** 2026-08-03
**Status:** Approved

## Overview

Host creates an Among Us lobby on a custom region -> mod detects it -> launcher uploads
lobby + mod set to self-hosted backend -> Discord bot posts `amonglauncher://join?code=...`
-> joiner clicks -> launcher sets up/downloads mods/launches -> mod joins the lobby.
Host mod changes -> backend pushes kick + rejoin to connected launchers automatically.

Scope: **launcher + Among API mod only**. The backend and Discord bot are owned by the
user; this spec defines the contract they must implement.

## Components

| Component | Where | New responsibilities |
|-----------|-------|---------------------|
| Among API mod | `Among API\` | Lobby-create detection hook (host), direct join handler (joiner), inbound IPC dispatch (currently missing), actually emit `game_ready` |
| Launcher | `Among Launcher\` | `amonglauncher://` URI scheme, `LobbyJoinService` (resolve -> install -> launch -> join), `LobbyCommandService` (WebSocket kick/rejoin), mod-set diff vs installed plugins |
| Backend (user-owned) | external | Store lobby state (code -> mod set, host, connected users), REST endpoints, WebSocket push |
| Discord bot (user-owned) | external | Post the invite link (via backend) |

## Data Flow

### Host creates lobby
1. Host creates lobby in game (code `ALSKDJ`, custom region).
2. Mod's Harmony hook detects lobby creation -> sends IPC `lobby_created` to launcher: `{ code, region, regionIp }`.
3. Launcher reads installed mods (BepInEx/plugins DLLs + metadata) -> `POST /lobby` to backend: `{ hostUserId, code, region, modSet }`.
4. Backend stores lobby, asks bot to post invite `amonglauncher://join?code=ALSKDJ`.
5. If a previous lobby from this host had connected users and the mod set differs -> backend pushes `rejoin` to each connected launcher.

### Joiner clicks link
1. OS launches launcher with URI `amonglauncher://join?code=ALSKDJ` (scheme registered on install).
2. Launcher parses code, authenticates with Discord OAuth token -> `GET /lobby/{code}` -> `{ modSet, region }`.
3. Launcher does full setup if missing (copy game, BepInEx, AmongApi), ensures mods match `modSet`, launches game.
4. Mod connects pipe -> sends `game_ready`.
5. Launcher opens WebSocket to backend (identifies as this Discord user, declares "in lobby ALSKDJ").
6. Launcher sends IPC `join_lobby` to mod: `{ code, region, regionIp }`.
7. Mod sets region, calls `AmongUsClient.JoinOnlineGame(code)`.

### Host mod change (kick + reconnect)
1. Host changes mods in launcher, relaunches game, creates new lobby (steps 1-4 of host flow). Backend sees new mod set.
2. Backend pushes `rejoin` `{ lobbyCode, modSet }` via WebSocket to each previously-connected launcher.
3. Each launcher kills the running game, installs the new mod set, relaunches, reconnects, rejoins — fully automatic.
4. "Kick specific player" = backend closes that launcher's WebSocket / pushes `kick`; launcher kills the game.

## IPC Protocol Additions

- Mod -> Launcher: `lobby_created { code, region, regionIp }`, `join_lobby_result { success, error? }`
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
   and emit `lobby_created` once.
2. **Direct join (joiner):** on `join_lobby`, register/select the custom region via `ServerManager`,
   decode the lobby code, call the game's join API.

### Research spike (gates mod-join coding)
- Exact `AmongUsClient.JoinOnlineGame` signature for the installed game version.
- How to register/select a custom region via `ServerManager`.
- Code -> int decoding (`GameCode` class).
- Reference strategy for the game's IL2CPP assemblies in the mod csproj and CI `build.yml`.

## Launcher Work

- **URI scheme registration:** `amonglauncher` -> launcher exe in registry, registered silently at
  startup if absent. Single-instance mechanism routes a second URI launch to the running instance.
- **`LobbyJoinService`:** parse URI -> code; `GET /lobby/{code}`; full setup if missing; diff installed
  mods vs `modSet` and download missing via existing `DownloadModAsync` pipeline; launch game; wait
  for `game_ready`; send `join_lobby`.
- **`LobbyCommandService`:** WebSocket to backend, identify via Discord token, declare lobby membership;
  handle `kick` and `rejoin`; reconnect with backoff and re-declare on reconnect. Structured so the
  transport could be swapped for polling later.
- **Config:** add `BackendUrl`.

## Error Handling

- Lobby fetch fails / code not found: launcher shows status message, does not launch game.
- Join fails (lobby full, wrong region, game error): mod sends `join_lobby_result { success: false, error }`;
  launcher surfaces it. Max 1 retry with brief delay.
- Game never reaches `game_ready`: timeout (e.g. 90s), then abort with message.
- WebSocket drops: reconnect with backoff; re-declare lobby membership on reconnect.
- Backend unreachable at lobby creation: host's mod retries `lobby_created` up to N times; if still down,
  the lobby simply isn't posted (game still works).

## Testing

- IPC unit tests: `join_lobby` handler on the mod side (mocked game client), `lobby_created` emit.
- Deep link: register scheme, launch with URI arg, verify single-instance routing.
- Join pipeline: with a real backend stub, verify mod-set diff + download + launch sequence.
- Manual end-to-end: host creates lobby -> invite posted -> joiner clicks -> joins; then mod-change -> kick -> reconnect.
- Research spike deliverables verified before writing mod-join code.

## Open Decisions

- Backend request/response shapes: drafted in the implementation plan; adjust to match the user's backend.
- "Launcher closed but game running" kick gap: **accepted**. Kick only reaches launchers with the launcher
  running; the mod does not hold a second socket.
- URI format: `amonglauncher://join?code=ALSKDJ`.
- Single-instance deep-link routing confirmed acceptable.
