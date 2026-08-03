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

Every message (request or response) must have this top-level structure:

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

## Connection Lifecycle

1. **AmongLauncher** starts a `PipeServer` listening on `AmongLauncher.IPC` when the app launches.
2. **AmongAPI** (inside Among Us) starts a `PipeClient` on plugin load, connecting to `AmongLauncher.IPC`.
3. Both sides keep the connection alive and exchange messages.
4. If Among Us closes, the client disconnects and the server detects it — ready for the next connection.

## Message Types

### Launcher → AmongAPI (Requests)

#### `uninstall_mod`
Tell the mod to remove an installed mod.

```json
{
  "type": "uninstall_mod",
  "id": "e5f6g7h8",
  "payload": {
    "modId": "aunlocker",
    "fileName": "AUnlocker.dll"
  }
}
```

**Response:** `mod_uninstalled_ack`

#### `get_mod_status`
Request the current mod status from AmongAPI.

```json
{
  "type": "get_mod_status",
  "id": "i9j0k1l2"
}
```

**Response:** `mod_status_response`

#### `restart_game`
Tell AmongAPI the launcher is about to restart the game.

```json
{
  "type": "restart_game",
  "id": "m3n4o5p6",
  "payload": {
    "reason": "New mod installed, restart required"
  }
}
```

**Response:** `restart_ack`

---

### AmongAPI → Launcher (Requests)

#### `install_mod`
Tell the launcher to download and install a mod DLL. Works regardless of game state — the launcher downloads to `BepInEx/plugins/` immediately.

```json
{
  "type": "install_mod",
  "id": "a1b2c3d4",
  "payload": {
    "modId": "aunlocker",
    "downloadUrl": "https://github.com/astra1dev/AUnlocker/releases/latest/download/AUnlocker.dll",
    "fileName": "AUnlocker.dll"
  }
}
```

**Response:** `install_mod_ack`

The launcher will also broadcast `mod_installed` when the download completes:
```json
{
  "type": "mod_installed",
  "id": "x1y2z3w4",
  "payload": {
    "modId": "aunlocker",
    "fileName": "AUnlocker.dll",
    "success": true
  }
}
```

#### `restart_after_install`
Tell the launcher to kill the game, wait for all pending mod installs to finish, then restart Among Us.

```json
{
  "type": "restart_after_install",
  "id": "r5s6t7u8"
}
```

**Response:** `restart_ack`

The launcher will:
1. Kill the Among Us process immediately
2. Wait for all pending `install_mod` downloads to complete
3. Automatically relaunch Among Us when all installs are done

#### `mod_status`
Report the current loaded mod status to the launcher.

```json
{
  "type": "mod_status",
  "id": "q7r8s9t0",
  "payload": {
    "loaded": true,
    "modName": "AUnlocker",
    "modVersion": "1.3.1"
  }
}
```

**Response:** `mod_status_ack`

#### `download_progress`
Report mod download progress to the launcher UI.

```json
{
  "type": "download_progress",
  "id": "u1v2w3x4",
  "payload": {
    "modId": "aunlocker",
    "percent": 65,
    "bytesDownloaded": 131072,
    "totalBytes": 201472
  }
}
```

**Response:** `download_progress_ack` (or no response)

#### `mod_installed`
Confirm a mod was installed successfully.

```json
{
  "type": "mod_installed",
  "id": "y5z6a7b8",
  "payload": {
    "modId": "aunlocker",
    "fileName": "AUnlocker.dll"
  }
}
```

**Response:** `mod_installed_ack`

#### `mod_uninstalled`
Confirm a mod was removed.

```json
{
  "type": "mod_uninstalled",
  "id": "c9d0e1f2",
  "payload": {
    "modId": "aunlocker",
    "fileName": "AUnlocker.dll"
  }
}
```

**Response:** `mod_uninstalled_ack`

#### `game_ready`
The game has finished loading and is at the main menu.

```json
{
  "type": "game_ready",
  "id": "g3h4i5j6",
  "payload": {
    "gameVersion": "2026.6.5",
    "amongApiVersion": "1.0.0"
  }
}
```

**Response:** `game_ready_ack`

#### `error`
Report an error to the launcher.

```json
{
  "type": "error",
  "id": "k7l8m9n0",
  "payload": {
    "message": "Failed to load mod: AUnlocker.dll",
    "code": "MOD_LOAD_FAILED"
  }
}
```

**Response:** none

#### `heartbeat`
Keep-alive ping.

```json
{
  "type": "heartbeat",
  "id": "o1p2q3r4"
}
```

**Response:** `heartbeat_ack`

---

### Response Types

| Response Type       | Sent By    | Description                        |
|---------------------|------------|------------------------------------|
| `heartbeat_ack`     | Launcher   | Heartbeat acknowledged             |
| `mod_status_ack`    | Launcher   | Mod status received                |
| `mod_installed_ack` | Launcher   | Mod installation confirmed         |
| `mod_uninstalled_ack` | Launcher | Mod uninstall confirmed            |
| `game_ready_ack`    | Launcher   | Game ready notification received   |
| `mod_status_response` | Launcher | Full mod status with installed list |
| `download_progress_ack` | AmongAPI | Download progress received         |
| `restart_ack`       | AmongAPI   | Restart request acknowledged       |

## Error Codes

| Code              | Description                        |
|-------------------|------------------------------------|
| `MOD_LOAD_FAILED` | A mod DLL failed to load           |
| `MOD_NOT_FOUND`   | Requested mod was not found        |
| `NETWORK_ERROR`   | Network request failed             |
| `DISK_ERROR`      | File write/read error              |
| `API_RATE_LIMIT`  | GitHub API rate limit exceeded     |
| `API_NOT_FOUND`   | GitHub API returned 404            |

## Launcher-side Implementation

The launcher runs `PipeServer` on the main window. Key integration points:

- **On client connect:** Update the mod status text to "AmongAPI connected"
- **On client disconnect:** Update the mod status text to "No mod loaded"
- **On `install_mod`:** Download mod DLL from `downloadUrl` to `BepInEx/plugins/`, broadcast `mod_installed` on completion
- **On `restart_after_install`:** Kill game process, wait for all pending installs, then relaunch
- **On `mod_status`:** Return the list of installed mods from `BepInEx/plugins/`
- **On `game_ready`:** Update the mod status text to "Game loaded — AmongAPI active"
- **On `download_progress`:** Update the progress bar in the UI
- **On `error`:** Show error in the mod status text

The pipe server is started in `MainWindow` constructor and broadcasts `launcher_ready` on window load.

## AmongAPI-side Implementation

The mod runs `PipeClient` in its `Plugin.Load()`:

```csharp
var client = new PipeClient();
await client.ConnectAsync();

// Request the launcher to install a mod
await client.SendMessageAsync("install_mod", new {
    modId = "aunlocker",
    downloadUrl = "https://github.com/astra1dev/AUnlocker/releases/latest/download/AUnlocker.dll",
    fileName = "AUnlocker.dll"
});

// Request game restart after installs complete
await client.SendMessageAsync("restart_after_install");

// Report game ready when loaded
await client.SendMessageAsync("game_ready", new { gameVersion = "2026.6.5" });
```

## Example Flow: API-Driven Mod Install

1. AmongAPI sends `install_mod` to the launcher with `downloadUrl` and `fileName`.
2. Launcher downloads the DLL to `BepInEx/plugins/` (works even if game is running).
3. Launcher broadcasts `mod_installed` when download completes.
4. Launcher refreshes the mod list in the UI.
5. AmongAPI receives `mod_installed` and loads the new DLL.

## Example Flow: Restart After Install

1. AmongAPI sends `restart_after_install` to the launcher.
2. Launcher kills the Among Us process immediately.
3. Launcher waits for all pending `install_mod` downloads to finish.
4. Once all installs complete, launcher automatically relaunches Among Us.
5. AmongAPI reconnects on the new game instance.
6. AmongAPI sends `game_ready` when the game loads.
7. Launcher updates the status badge to "Running" and shows "Game loaded — AmongAPI active".

## Example Flow: Launcher-Initiated Install

1. User clicks "Install" on a preset mod in the launcher UI.
2. Launcher downloads the mod DLL from GitHub.
3. Launcher writes the DLL to `BepInEx/plugins/`.
4. Launcher updates the mod list in the UI.
