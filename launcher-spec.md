# Launcher Spec

Exact technical details of how the **Among Launcher** accepts deep-links, how its local IPC server is configured, and the JSON handshake it expects from the in-game **Among API** mod.

> **Note on transport:** the internal local IPC is **not** TCP. It uses **Windows Named Pipes** on `localhost` (the `\\.\pipe\` namespace). There are two separate named pipes with distinct roles:
> - `AmongLauncher.IPC` — the bidirectional control channel between launcher and the in-game mod.
> - `AmongLauncher.Redirect` — used only for single-instance deep-link forwarding between two launcher processes.
>
> No socket port is used anywhere; there is no TCP port to configure.

---

## 1. Deep-Links

The launcher registers **two** URI schemes in the registry (`HKCU\Software\Classes\<scheme>`) via `DeepLinkHandler.RegisterProtocol()` (see `Among Launcher\Services\DeepLinkHandler.cs:64`). Both schemes point the shell's open command at the launcher EXE (`"<exe>" "%1"`).

### 1.1 Mod install deep-link

- **Scheme:** `amongus-launcher`
- **Host:** `install`
- **Format:**
  ```
  amongus-launcher://install?mods=<url1>,<url2>,<url3>
  ```
- **`mods`:** a comma-separated list of URL-encoded absolute HTTP(S) URLs to mod DLLs. See `DeepLinkHandler.Parse` (`DeepLinkHandler.cs:16`).
- **Behavior:** Downloads each listed DLL into `BepInEx/plugins/` (each `url` → the file named after the URL's path component), then launches the game via the main view. Handled by `MainWindow.HandleDeepLink`/`ShowDownloadModsModal` (`MainWindow.xaml.cs:518`).
- If the modded install / BepInEx is not present, a confirmation modal is shown instead of downloading.

### 1.2 Lobby join deep-link

- **Scheme:** `amonglauncher`
- **Host:** `join`
- **Format:**
  ```
  amonglauncher://join?code=ABCDEF
  ```
- **`code`:** the 6-character lobby code. Parsed by `DeepLinkHandler.TryParseJoin` (`DeepLinkHandler.cs:47`), which uppercases, trims, URL-decodes it, and validates length 4–8.
- **Behavior:** The full join pipeline (`MainWindow.HandleJoinLinkAsync` at `MainWindow.xaml.cs:213`):
  1. Fetch the lobby (`GET /lobby/{code}`) from the configured backend. If unconfigured or not found, an error modal is shown.
  2. Verify the modded install exists (`winhttp.dll` present in the modded folder).
  3. Sync the lobby's `modSet` into `BepInEx/plugins/`, downloading any missing DLLs.
  4. Kill any running game, launch Among Us, and wait up to 90s for the mod's `game_ready`.
  5. Broadcast `join_lobby` over the IPC pipe with `{ code, region, regionIp, regionPort }`.
  6. Connect the lobby WebSocket (`<BackendWssUrl>?code=...`).

### 1.3 Single-instance routing

A second instance launched with a deep-link argument forwards the raw deep-link string to the already-running primary instance using the **`AmongLauncher.Redirect`** pipe (`SingleInstance.cs`):
- Primary creates a global mutex `Global\AmongLauncher.SingleInstance`; only it runs `StartRedirectServer`, which listens on `AmongLauncher.Redirect` (inbound pipe) and reads one UTF-8 line per connection.
- Secondary instances call `ForwardDeepLink(deepLink)`: connect write-only to `AmongLauncher.Redirect` (2s connect timeout) and write the deep-link as a single line, then exit.
- On the primary, the received link fires `App.DeepLinkReceived`, which is dispatched to `MainWindow.HandleDeepLink`.

---

## 2. Local IPC Server Configuration

### 2.1 The control pipe (`AmongLauncher.IPC`)

Implemented by `PipeServer` (`Among Launcher\Ipc\PipeServer.cs:7`).

| Setting | Value |
|---------|-------|
| Type | **Windows Named Pipe** (server) |
| Pipe name | `AmongLauncher.IPC` |
| Direction | `InOut` (bidirectional) |
| Max instances | `1` |
| Transmission mode | `Byte` |
| Options | `Asynchronous` |
| Head-of-line capacity | single connection at a time (server loops: accept → handle → dispose → accept) |

The server is started once with `PipeServer.Start()` in `MainWindow`'s constructor (`MainWindow.xaml.cs:161`). It listens on `localhost`: client connects to `\\.\pipe\AmongLauncher.IPC`. Name resolution on the client side uses the local machine (`"."`) — see `PipeClient.ConnectAsync` (`Among API\Services\PipeClient.cs:44`).

### 2.2 Wire / frame format

Every frame is length-prefixed UTF-8 JSON:

```
[ 4 bytes: payload length (int32, little-endian) ][ N bytes: JSON payload ]
ReadMessage: BitConverter.ToInt32(header) → validate 0 < len ≤ 1 MB → read exactly len bytes.
SendMessage: BitConverter.GetBytes(data.Length) → write header → write payload → Flush().
```

### 2.3 JSON message envelope

```json
{
  "type": "<message_type>",
  "id": "<8_char_hex>",
  "timestamp": 1735689600000,
  "payload": { ... }
}
```

| Field | Type | Notes |
|-------|------|-------|
| `type` | string | required; routed to the registered handler |
| `id` | string | random 8-char hex, used to correlate request/response |
| `timestamp` | long | Unix epoch ms |
| `payload` | object | optional; omitted when `null` |

Messages sent by the launcher (launcher → mod) are built with `timestamp` and an `id`; the mod's responses (`<type>_ack`) echo the request `id`. Messages sent by the mod (mod → launcher) also carry `id`/`timestamp`, but the launcher's response handling does **not** echo them — see §4.

---

## 3. Handler Registration (server side)

The launcher registers message-type handlers with `RegisterHandler(type, handler)` in the `MainWindow` constructor (`MainWindow.xaml.cs:83`–`159`). When a frame arrives, `PipeServer.HandleConnectionAsync` (`PipeServer.cs:86`) reads `type`, looks up the handler, calls `handler(rootElement)`, and — **if the handler returns a non-null object** — serializes that object **directly** as the response frame (no special envelope wrapper, no `id`/`timestamp` echo).

The launcher's server-side handlers:

| `type` | Payload | Response |
|--------|---------|----------|
| `game_ready` | _(none sent by the mod today)_ | `{ "type": "game_ready_ack", "restart": false }` |
| `lobby_created` | `{ code, region }` | `{ "type": "lobby_created_ack" }` |
| `lobby_closed` | `{ code, reason }` | `{ "type": "lobby_closed_ack" }` |
| `player_joined` | `{ playerName, playerCount }` | none |
| `player_left` | `{ playerName, playerCount }` | none |
| `join_lobby_result` | `{ success, error? }` | none |

`region` in `lobby_created` is the display name only (e.g. `"NA"`, `"EU"`, `"ASIA"`, or a custom server label). The launcher does **not** send `regionIp` or `regionPort` upstream to the backend — those are internal to the IPC join flow.

---

## 4. Handshake Expected From the In-Game Mod

The launcher expects the Among API mod to act **as the IPC client** and to perform the following sequence (see `Among API\Plugin.cs:20` and `MainWindow`'s pipe handlers):

### 4.1 Connect

The mod connects a `NamedPipeClientStream` to `\\.\pipe\AmongLauncher.IPC` (pipe name `AmongLauncher.IPC`, `InOut`, async). The `PipeClient.ConnectAsync` logic retries up to 5 attempts with a 2s delay between failures (`PipeClient.cs:36`).

### 4.2 Announce readiness — `game_ready`

Immediately after connecting, the mod must send:

```json
{
  "type": "game_ready",
  "id": "8_char_hex",
  "timestamp": 1735689600000
}
```

**Expected launcher behavior:** sets the `_gameReadyTcs` used to unblock the join pipeline's 90s `WaitForGameReady` wait, updates the UI status to `"Game loaded — AmongAPI active"`, and replies with a frame whose serialized body is:

```json
{ "type": "game_ready_ack", "restart": false }
```

> This is the critical handshake for join requests: the launcher releases the join pipeline only when `game_ready` is received (or the 90s timeout / process-crash guard fires).

### 4.3 Report lobby state transitions (host only)

The mod polls game state (see `GameStateTracker.cs`) and sends these event messages up the pipe, **only for the local client that is the host**:

- **Lobby created / reposted:**
  ```json
  {
    "type": "lobby_created",
    "id": "8_char_hex",
    "timestamp": 1735689600000,
    "payload": { "code": "ABCDEF", "region": "NA" }
  }
  ```
  The launcher mirrors the lobby to the backend (using `region` as the display name), starts the heartbeat, opens the lobby WebSocket, and (if the signed-in user is the host) shows the host control panel. `regionIp`/`regionPort` are not sent upstream; they are internal to the IPC `join_lobby` flow.

- **Lobby closed / disbanded:**
  ```json
  {
    "type": "lobby_closed",
    "id": "8_char_hex",
    "timestamp": 1735689600000,
    "payload": { "code": "ABCDEF", "reason": "" }
  }
  ```
  `reason` is `""` for a normal leave and `"disband"` for the in-game `/disband` chat command. The launcher disbands the lobby on the backend and tears down heartbeat/WebSocket/host panel.

- **Player joined / left:**
  ```json
  {
    "type": "player_joined",
    "id": "8_char_hex",
    "timestamp": 1735689600000,
    "payload": { "playerName": "<unknown>", "playerCount": 4 }
  }
  ```
  `playerName` is currently `"<unknown>"` because the tracker cannot read names; the launcher keys its live player list off `playerCount`. Same shape for `player_left`.

### 4.4 Handle commands from the launcher

The mod must register a handler for **`join_lobby`** and execute it:

- Launcher sends:
  ```json
  {
    "type": "join_lobby",
    "id": "8_char_hex",
    "timestamp": 1735689600000,
    "payload": { "code": "ABCDEF", "region": "NA", "regionIp": "127.0.0.1", "regionPort": 22023 }
  }
  ```
- Mod should join the lobby in-game and reply with a handler result `{ success, error }` (sent as the `<type>_ack` frame echoing the request `id`), and also broadcast `join_lobby_result`:
  ```json
  {
    "type": "join_lobby_result",
    "id": "8_char_hex",
    "timestamp": 1735689600000,
    "payload": { "success": true, "error": null }
  }
  ```
  The launcher's `join_lobby_result` handler surfaces `error` in the UI when `success` is false.

### 4.5 Disconnect

When the game closes, the mod's `PipeClient` read loop exits and fires `Disconnected`; the launcher's server sees the stream end, disposes the connection, updates the connection status to disconnected, and listens for the next client.

---

## 5. Key Behaviors / Edge Cases

- **Port fallback in `join_lobby`:** the IPC `join_lobby` message includes `regionIp`/`regionPort` for the in-game joiner; the mod falls back to port `22023` when `regionPort` is missing or `<= 0`.
- **Response asymmetry:** the mod always gets an `id`-echoed `<type>_ack` for its own sends, but launcher responses back to the mod do **not** echo `id`/`timestamp` (they are the handler's raw serialized object). The mod therefore ignores reply bodies and returns `null` after a send.
- **1 MB cap:** frames larger than 1,048,576 bytes are rejected.
- **Single connection:** the `AmongLauncher.IPC` server accepts one client at a time (max instances = 1).
- **Deep-link availability guards:** joins require a signed-in/configured backend (`ServerUrl` not containing `yourserver.com`) and an installed modded copy with `winhttp.dll`.
- Two schemes are registered; only `amonglauncher://join` and `amongus-launcher://install` are meaningful to the parser. Anything else is ignored.

---

## 6. Cross-Service Contract

The launcher communicates with two external services. This section documents the data shapes the launcher sends/receives so other services can align.

### 6.1 Python Backend (FastAPI — `backend-spec.md`)

The launcher's `LobbyBackendClient` sends requests to `ServerUrl` (e.g. `https://yourserver.com/api/v1/`).

**Create / refresh lobby (`POST /api/v1/lobbies`):**

The launcher sends:
```json
{
  "code": "ABCD",
  "region": "NA",
  "host": "Alice",
  "mod_type": "modded",
  "mods": [{"name": "ExampleMod", "version": "1.0.0", "file_hash": null}]
}
```

| Field | Type | Notes |
|-------|------|-------|
| `code` | string | Lobby code (uppercase) |
| `region` | string | Region display name |
| `host` | string | Host player's display name (from Discord auth or config) |
| `mod_type` | string | Always `"modded"` (launcher only creates modded lobbies) |
| `mods` | array | Installed mod DLLs; `name` = file name, `version`/`file_hash` may be null |

The Python backend returns:
```json
{
  "code": "ABCD",
  "region": "NA",
  "host": "Alice",
  "mod_type": "modded",
  "mods": [{"id": "abc123", "name": "ExampleMod", "version": "1.0.0", "file_hash": "...", "size": 2048, "url": "/api/v1/mods/abc123/download"}],
  "players": [{"id": "host", "name": "Alice", "is_host": true}],
  "last_heartbeat": "2026-08-05T12:34:56.789012+00:00"
}
```

The launcher maps `mods` → internal `ModSetEntry` list (for mod-set sync) and uses `players.Count` for the player count.

**Fetch lobby (`GET /api/v1/lobbies/{code}`):** returns a single `LobbyResponse` (same shape). Used for mod-set sync during join. The launcher converts `mods[].name` → `ModSetEntry.FileName`.

**Heartbeat (`POST /api/v1/lobbies/{code}/heartbeat`):** no body. Response: `{ "ok": true, "error": null }`.

**Repost (`POST /api/v1/lobbies/{code}/repost`):** no body. Response: `{ "ok": true, "error": null }`.

**Kick (`POST /api/v1/lobbies/{code}/kick`):**
```json
{ "player_id": "discord_user_id" }
```
Response: `{ "ok": true, "error": null }`.

**Disband (`DELETE /api/v1/lobbies/{code}`):** no body. Response: `{ "ok": true, "error": null }`.

**Mod download (`GET /api/v1/mods/{id}/download`):** public, returns binary file with `Content-Disposition` filename.

**WebSocket (`WS /api/v1/ws/{code}?client_id={id}`):** the launcher connects with optional `Authorization: Bearer <token>` header. Incoming messages:
- `{"action":"kick","payload":{"target_id":"...","reason":"..."}}` → launcher kills the game.
- `{"action":"disband","payload":{"code":"..."}}` → launcher tears down lobby state.

**Auth:** all lobby REST routes require `Authorization: Bearer <DiscordAccessToken>` when `AUTH_TOKEN` is configured on the backend. The launcher sends this when `config.DiscordAccessToken` is non-empty.

### 6.2 Frontend / Discord Bot (`frontend-spec.md`)

The Discord bot and frontend handle their own integration (forum thread creation, role pings, tag application). The launcher does not interact with them directly — the backend bridges the data.
