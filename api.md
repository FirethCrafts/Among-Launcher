# AmongLauncher ↔ AmongAPI IPC Protocol

Local inter-process communication between the AmongLauncher EXE and the AmongAPI BepInEx mod running inside Among Us.

## Overview

- **Transport:** Windows Named Pipes
- **Pipe Name:** `AmongLauncher.IPC`
- **Direction:** Bidirectional — either side can send messages at any time
- **Encoding:** UTF-8 JSON, length-prefixed frames

## Wire Format

Every message on the pipe is a single **frame**:

```
[ 4 bytes: payload length (int32, little-endian) ][ N bytes: JSON payload ]
```

- Maximum payload size: **1 MB**
- After sending a frame, the sender calls `Flush()`
- The receiver reads the 4-byte header, then reads exactly that many bytes for the payload

## JSON Message Envelope

Every message (request or broadcast) sent by either side has this top-level structure:

```json
{
  "type": "<message_type>",
  "id": "<unique_8_char_id>",
  "timestamp": 1735689600000,
  "payload": { ... }
}
```

| Field       | Type     | Description                                              |
|-------------|----------|----------------------------------------------------------|
| `type`      | `string` | Message type (required)                                  |
| `id`        | `string` | Random 8-character hex ID used to correlate request/response |
| `timestamp` | `long`   | Unix epoch in milliseconds                               |
| `payload`   | `object` | Message-specific data (optional for some types)          |

**Responses** differ slightly by side:

- **AmongAPI (PipeClient)** echoes the request `id` and adds a `timestamp` to its `<type>_ack` responses.
- **AmongLauncher (PipeServer)** serializes the handler's returned object directly (e.g. `{"type":"game_ready_ack","restart":false}`) and does **not** echo `id`/`timestamp`. Because of this, the plugin's `SendMessageAsync` cannot correlate launcher responses and simply returns `null` after its 10-second timeout; the plugin ignores the result of every send it makes.

## Connection Lifecycle

1. **AmongLauncher** starts a `PipeServer` listening on `AmongLauncher.IPC` when the app launches.
2. **AmongAPI** (inside Among Us) starts a `PipeClient` on plugin load, connecting to `AmongLauncher.IPC`.
3. Both sides keep the connection alive and exchange messages.
4. If Among Us closes, the client disconnects and the server detects it — ready for the next connection.
5. The launcher broadcasts `launcher_ready` after the main window finishes loading; the plugin broadcasts `game_ready` once the game reaches the main menu.

## Message Types

Legend: ✅ implemented & sent, 🔶 handled but no live sender/receiver today, ❌ removed / never implemented.

### Launcher → AmongAPI

#### `launcher_ready` ✅ (broadcast)
Sent once when the launcher window finishes loading, as a handshake with the plugin.

```json
{ "type": "launcher_ready", "id": "a1b2c3d4", "timestamp": 1735689600000 }
```

No payload. **Response:** none.

#### `join_lobby` ✅ (broadcast)
Asks the plugin to join a lobby directly in-game. Sent during a deep-link join or a WebSocket-triggered rejoin.

```json
{
  "type": "join_lobby",
  "id": "e5f6g7h8",
  "timestamp": 1735689600000,
  "payload": {
    "code": "ABCDEF",
    "region": "NA",
    "regionIp": "127.0.0.1",
    "regionPort": 22023
  }
}
```

The plugin responds with `join_lobby_ack` (echoed id) and also broadcasts the result as `join_lobby_result`.

**Response:** `join_lobby_ack` with `{ "success": true, "error": null }`

#### `kick` 🔶
Tells the plugin that a player was kicked. The plugin raises its `KickRequested` event (no handler is registered, so no ack is sent).

```json
{
  "type": "kick",
  "id": "i9j0k1l2",
  "timestamp": 1735689600000,
  "payload": { "reason": "Vote kicked" }
}
```

`reason` is optional. The launcher currently routes host kicks through the self-hosted backend / WebSocket path; the IPC `kick` handler exists on the plugin side but is not yet sent by the launcher.

**Response:** none.

#### `mod_installed` ❌ (removed)
Announced that a mod DLL finished downloading. Removed — it was the completion notice for the removed `install_mod` flow; the launcher no longer broadcasts it.

#### `restart` ❌ (removed)
Special-case message that made the plugin's `PipeClient` stop its read loop. Removed — the launcher never sent it and the plugin's read loop no longer special-cases it.

---

### AmongAPI → Launcher

#### `game_ready` ✅
Sent once after the plugin connects and the game is at the main menu. Currently sent with **no payload**.

```json
{ "type": "game_ready", "id": "q7r8s9t0", "timestamp": 1735689600000 }
```

**Response:** `game_ready_ack` with `{ "restart": false }`

#### `lobby_created` ✅
The host entered or created a lobby in-game. The launcher mirrors the lobby to the backend, starts the heartbeat, opens the WebSocket, and shows the host control panel. Also re-sent by the plugin's `/repost` chat command.

```json
{
  "type": "lobby_created",
  "id": "u1v2w3x4",
  "timestamp": 1735689600000,
  "payload": {
    "code": "ABCDEF",
    "region": "NA",
    "regionIp": "127.0.0.1",
    "regionPort": 22023,
    "host": "PlayerName",
    "playerCount": 1
  }
}
```

- `region` is the selected region's display name resolved from `ServerManager.CurrentRegion.Name` (e.g. `"NA"`, `"EU"`, `"ASIA"`, or a custom server label). `"UNKNOWN"` when the game hasn't loaded the server manager yet.
- `host` is the local player's display name from `PlayerControl.LocalPlayer.Data.PlayerName`. `"UNKNOWN"` when unavailable.
- `regionIp`/`regionPort` are currently sent empty/`0`; the launcher falls back to port `22023` when `regionPort` is missing or non-positive.

**Response:** `lobby_created_ack`

#### `lobby_closed` ✅
The host left or the lobby was disbanded. The launcher disbands it on the backend and tears down the heartbeat/WebSocket/host panel.

```json
{
  "type": "lobby_closed",
  "id": "y5z6a7b8",
  "timestamp": 1735689600000,
  "payload": { "code": "ABCDEF", "reason": "disband" }
}
```

`reason` is `""` for a normal leave and `"disband"` for the `/disband` chat command.

**Response:** `lobby_closed_ack`

#### `player_joined` ✅
A player joined the lobby.

```json
{
  "type": "player_joined",
  "id": "c9d0e1f2",
  "timestamp": 1735689600000,
  "payload": { "playerName": "<unknown>", "playerCount": 4 }
}
```

The current tracker cannot read player names, so `playerName` is `"<unknown>"`; the launcher uses `playerCount` for its live player list.

**Response:** none.

#### `player_left` ✅
A player left the lobby. Same payload shape as `player_joined`.

**Response:** none.

#### `join_lobby_result` ✅
Result of a `join_lobby` request, surfaced to the launcher UI on failure.

```json
{
  "type": "join_lobby_result",
  "id": "g3h4i5j6",
  "timestamp": 1735689600000,
  "payload": { "success": true, "error": null }
}
```

`error` is present only when `success` is `false`.

**Response:** none.

#### `install_mod` ❌ (removed)
Historically asked the launcher to download a mod DLL to `BepInEx/plugins/`. The plugin no longer sends this and the launcher no longer registers a handler — mod installs now flow through the launcher's join/mod-set pipeline.

#### `restart_after_install` ❌ (removed)
Historically asked the launcher to kill the game and relaunch after installs. Removed — no sender, no handler.

#### `mod_status` ❌ (removed)
Historically requested the launcher's installed mod list. Removed — no sender, no handler.

#### `heartbeat` ❌ (removed)
Keep-alive ping. Removed — the plugin never sent it and the launcher no longer registers a handler.

#### `error` ❌ (removed)
Historically reported an error to the launcher. Removed — the plugin never sends it and the launcher no longer registers a handler.

#### `download_progress` ❌ (removed)
Progress reporting for `install_mod`. Removed along with `install_mod`.

#### `mod_uninstalled` ❌ (removed)
Historically confirmed a mod removal. Removed — no sender, no launcher uninstall flow.

---

### Removed / never implemented

The following types appear in earlier drafts of this protocol but were never implemented and are removed:

| Type         | Direction      | Status                          |
|--------------|----------------|---------------------------------|
| `uninstall_mod` | Launcher → AmongAPI | ❌ Removed — never implemented |
| `get_mod_status` | Launcher → AmongAPI | ❌ Removed — never implemented |
| `restart_game`   | Launcher → AmongAPI | ❌ Removed — never implemented |
| `install_mod`    | AmongAPI → Launcher | ❌ Removed — no sender, no handler |
| `restart_after_install` | AmongAPI → Launcher | ❌ Removed — no sender, no handler |
| `mod_status`     | AmongAPI → Launcher | ❌ Removed — no sender, no handler |
| `heartbeat`      | AmongAPI → Launcher | ❌ Removed — no sender, no handler |
| `error`          | AmongAPI → Launcher | ❌ Removed — no sender, no handler |
| `download_progress` | AmongAPI → Launcher | ❌ Removed — no sender, no handler |
| `mod_uninstalled` | AmongAPI → Launcher | ❌ Removed — no sender, no handler |

---

### Response Types

| Response Type        | Sent By    | Description                                                            |
|----------------------|------------|------------------------------------------------------------------------|
| `game_ready_ack`     | Launcher   | `{ restart: false }`                                                   |
| `lobby_created_ack`  | Launcher   | Lobby mirrored to backend                                              |
| `lobby_closed_ack`   | Launcher   | Lobby disbanded on backend                                             |
| `join_lobby_ack`     | AmongAPI   | `{ success, error }` reply to `join_lobby`, echoes the request id      |

## Launcher-side Implementation

The launcher runs `PipeServer` on the main window. Key integration points:

- **On client connect/disconnect:** Update the "AmongAPI connected" connection status
- **On `launcher_ready`:** Broadcast once the window loads
- **On `game_ready`:** Update status text to "Game loaded — AmongAPI active", respond `game_ready_ack`
- **On `lobby_created`:** Mirror the lobby to the backend, start heartbeat, open WebSocket, show host panel; respond `lobby_created_ack`
- **On `lobby_closed`:** Disband the lobby on the backend, stop heartbeat/WebSocket; respond `lobby_closed_ack`
- **On `player_joined`/`player_left`:** Update the live player list (local only)
- **On `join_lobby_result`:** Surface join errors to the UI

## AmongAPI-side Implementation

The mod runs `PipeClient` in its `Plugin.Load()`:

```csharp
var client = new PipeClient(Log);
await client.ConnectAsync();

// Report game ready when loaded
await client.SendMessageAsync("game_ready");

// Report lobby / player state transitions to the launcher
var tracker = new GameStateTracker(Log);
tracker.LobbyCreated += (_, info) => _ = pipe.SendMessageAsync("lobby_created", info);
tracker.LobbyClosed += (_, reason) => _ = pipe.SendMessageAsync("lobby_closed", new { code, reason });
tracker.PlayerJoined += (_, p) => _ = pipe.SendMessageAsync("player_joined", p);
tracker.PlayerLeft += (_, p) => _ = pipe.SendMessageAsync("player_left", p);
tracker.Start();

// Handle direct lobby joins from the launcher
pipe.RegisterHandler("join_lobby", async element =>
{
    var result = await joiner.JoinAsync(code, region, regionIp, regionPort);
    _ = pipe.SendMessageAsync("join_lobby_result", new { success = result.Success, error = result.Error });
    return new { success = result.Success, error = result.Error };
});
```

## Example Flow: Join Lobby via Deep Link

1. User opens `amonglauncher://join?code=ABCDEF` (a second instance forwards it to the running one via the redirect pipe).
2. Launcher fetches the lobby from the backend (code, region, region IP/port, mod set).
3. Launcher syncs the lobby's mod set into `BepInEx/plugins/` (downloading any missing DLLs with retry).
4. Launcher kills any running game, launches Among Us, and waits for the plugin's `game_ready`.
5. Launcher broadcasts `join_lobby { code, region, regionIp, regionPort }`.
6. Plugin joins in-game and broadcasts `join_lobby_result { success, error }`.
7. Launcher connects the lobby WebSocket and shows the live status.

## Example Flow: Host Lobby Mirroring

1. Host enters a lobby in-game; the plugin broadcasts `lobby_created { code, region, regionIp, regionPort }`.
2. Launcher creates the lobby on the backend, starts the heartbeat, opens the WebSocket, and shows the host control panel.
3. Player joins/leaves are reflected via `player_joined`/`player_left`.
4. The host can repost or disband from the panel (or use `/repost` / `/disband` chat commands); disband sends `lobby_closed`.
5. The launcher disbands the lobby on the backend and tears everything down.

## Example Flow: Mod-Set Sync During Join

1. The launcher fetches the lobby's `modSet` from the backend.
2. `ModSetSync` diffs the target set against `BepInEx/plugins/` and downloads any missing DLLs (with file-lock retry).
3. The launcher launches Among Us and waits for the plugin's `game_ready`.
4. Mod installs now flow entirely through this join/mod-set pipeline — there is no separate `install_mod` request type.


---

# Among Backend HTTP + WebSocket API

REST + WebSocket contract implemented by the self-hosted `Among Backend` server. The launcher talks to it for lobby mirroring, join resolution, heartbeats, and live kick/rejoin pushes.

## Base URL

`ServerUrl` in launcher config (e.g. `https://yourserver.com/api`). All REST requests from the launcher set `Authorization: Bearer <DiscordAccessToken>` when a token is present (the backend may ignore or use it for identity).

## REST Endpoints

| Method | Endpoint | Body | Purpose |
|--------|----------|------|---------|
| POST | `/lobby` | `{ code, region, regionIp, regionPort, modSet, hostUserId }` | Create/register a lobby. If the host previously ran a lobby with connected launchers and the mod set differs, pushes `rejoin` to the old lobby's guests. Re-POSTing an existing code refreshes it. |
| GET | `/lobby/{code}` | � | Fetch a lobby ? `{ code, region, regionIp, regionPort, modSet, hostUserId, playerCount }`. 404 if not found. |
| POST | `/lobby/{code}/repost` | � | Refresh the Discord embed. |
| POST | `/lobby/{code}/kick` | — | Push `kick` to the lobby's connected launchers over WebSocket (forces them to leave). |
| DELETE | `/lobby/{code}` | — | Disband/delete a lobby and remove its Discord embed. |
| POST | `/lobby/{code}/heartbeat` | — | Keepalive from the host launcher. Lobbies that stop heartbeating are auto-expired after the grace period. |

`modSet` entries: `{ fileName, downloadUrl, sha256?, version? }`.

## WebSocket

- **Endpoint:** `<BackendWssUrl>?code={lobbyCode}` (e.g. `wss://yourserver.com/ws?code=ALSKDJ`).
- **Auth:** `Authorization: Bearer <DiscordAccessToken>` header.
- **Server ? launcher messages:**
  - `kick` � `{ "type": "kick", "reason": "..." }` ? launcher kills the game.
  - `rejoin` � `{ "type": "rejoin", "payload": { "lobbyCode", "modSet", "region", "regionIp", "regionPort" } }` ? launcher installs the new mod set, relaunches, and rejoins.
- The launcher reconnects with backoff on drop and re-declares its lobby via the `?code=` param.

## Heartbeat Expiry

`POST /lobby/{code}/heartbeat` must be sent by the host while hosting. The backend expires a lobby (deleting state + embed) if no heartbeat arrives within `Lobby:HeartbeatGraceSeconds` (default 90s).

## Discord Embed

When `Discord:WebhookUrl` is configured in `appsettings.json`, the backend posts a live invite embed with a **Join Lobby** button (`amonglauncher://join?code=...`), the player count, region, and host. The embed is edited on player-count/`/repost` changes and deleted on disband/expiry. Without the webhook, the backend still works locally (embed is a no-op).
