# Lobby Forum Bot WebSocket Integration — Design

**Date:** 2026-08-04
**Status:** Approved (Approach A — dedicated `LobbyBotClient` service + MainWindow wiring)

## Overview

Integrate the Among Launcher with the Lobby Forum Bot WebSocket protocol so
that hosting a game triggers an automated forum thread. Adds three persisted
config fields, expands the Settings UI, auto-detects modded vs vanilla lobbies
from the modded install, and sends the bot's JSON payload over a WebSocket
connection. The bot closes the socket immediately after its ACK, so each
dispatch is a one-shot connect → send → await-ACK cycle with no auto-reconnect.

**Non-goals:**
- No forum-thread posting logic inside the launcher — the bot owns that; the
  launcher only sends the payload.
- No lobby-fullness/player-count updates over the bot socket in this iteration.
- No changes to the existing backend (`LobbyBackendClient` /
  `LobbyWebSocketClient`) flows.
- No new NuGet packages — `ClientWebSocket` (already used) covers this.

## Architecture

### Config: `LauncherConfig`

Add three persisted properties (JSON names match property names):

```csharp
public string BotWsEndpoint { get; set; } = "ws://127.0.0.1:8080";
public string ModdedRoleId { get; set; } = string.Empty;
public string VanillaRoleId { get; set; } = string.Empty;
```

Defaults are backward compatible: existing config files load with the default
endpoint and empty role IDs.

### SettingsView

Three new cards/rows below the existing "Modded Install Path" card, following
the existing `GlassCard` + `GlassInput` pattern:

- "BOT WS ENDPOINT" → `BotWsEndpointTextBox`
- "MODDED ROLE ID" → `ModdedRoleIdTextBox`
- "VANILLA ROLE ID" → `VanillaRoleIdTextBox`

Load all three in `SettingsView_Loaded`. Save on the same trigger the existing
`ServerUrlTextBox` uses (text change/lost focus — match current behavior) so
edits persist to `config.json` immediately. No new save button required unless
the existing Server URL field already uses one.

### `LobbyTypeDetector` (new)

Pure static helper in `Services/Lobby/LobbyTypeDetector.cs`:

```csharp
public static class LobbyTypeDetector
{
    private static readonly HashSet<string> ExcludedDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "AmongApi.dll",
        "0Harmony.dll", "AsmResolver.dll", "BepInEx.Core.dll",
        "BepInEx.Preloader.Core.dll", "BepInEx.Unity.Common.dll",
        "BepInEx.Unity.IL2CPP.dll"
    };

    public static string DetectLobbyType(string moddedPath)
    {
        var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir)) return "vanilla";

        var hasMod = Directory.EnumerateFiles(pluginsDir, "*.dll")
            .Any(f => !ExcludedDlls.Contains(Path.GetFileName(f)));
        return hasMod ? "modded" : "vanilla";
    }
}
```

Rules:
- Inspects `%LocalAppData%\AmongLauncher\ModdedAmongUs\BepInEx\plugins`.
- Any `*.dll` present that is **not** `AmongApi.dll` or a named BepInEx core
  assembly counts as modded → returns `"modded"`. Otherwise `"vanilla"`.
- `*.dll` enumeration naturally excludes disabled mods (renamed `*.disabled`).
- Missing plugins dir → `"vanilla"`.
- Returns exactly `"modded"` / `"vanilla"` as the protocol expects.

### `LobbyBotClient` (new)

New `Services/Lobby/LobbyBotClient.cs`, modeled on `LobbyWebSocketClient`:

- `Task ConnectAsync(string endpoint, CancellationToken ct)` — opens a
  `ClientWebSocket` to `endpoint` for a single dispatch cycle.
- `Task SendLobbyCreatedAsync(LobbyBotPayload payload)` — serializes the
  payload to the exact JSON, sends one frame, then reads the bot's response
  frame, and returns the parsed `LobbyBotResponse`. If the socket is not open,
  connects first so a payload always attempts delivery.
- `void Disconnect()` — cancels any pending receive/keep-alive work and
  disposes the socket.
- All socket operations are wrapped in try/catch; failures never propagate to
  the caller.

**Connection lifecycle (bot protocol):** the bot server calls `close()`
immediately after transmitting its ACK response. Server-initiated closure
after response delivery is treated as **normal, successful completion**, not a
connection drop. Therefore **no auto-reconnect loop** is used for the bot
dispatch cycle — the socket is a one-shot per `SendLobbyCreatedAsync` call.
`ConnectAsync` may attempt the initial connection with bounded retries, but
once a response is received (or the socket is closed by the server post-ACK),
the cycle ends cleanly without reconnect.

Payload record:

```csharp
public record LobbyBotPayload(
    string Code,
    string Region,
    string Host,
    string Mod,
    string RoleId,
    string[] AppliedTags);
```

Response record:

```csharp
public record LobbyBotResponse(bool Ok, string? Error);
```

Serialized (camelCase) exactly as the requirement specifies:

```json
{
  "code": "<CODE>",
  "region": "<REGION>",
  "host": "<HOST>",
  "mod": "modded | vanilla",
  "roleId": "<ROLE_ID>",
  "appliedTags": []
}
```

**JSON serialization:** `LobbyBotClient` must configure `System.Text.Json` with
camelCase property naming so the C# PascalCase record properties serialize to
lowercase JSON keys:

```csharp
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};
```

**Response handling:** after sending the frame, `SendLobbyCreatedAsync`
executes `ReceiveAsync` to await the bot's response frame. It parses
`{ "ok": bool, "error": string }` into `LobbyBotResponse` and logs the result
to the launcher log viewer via `LauncherLog.Write(...)`. The subsequent socket
closure by the bot is handled gracefully (treated as the normal end of the
dispatch cycle, no reconnect, no error surfaced).

### MainWindow wiring

- Field: `private readonly Services.Lobby.LobbyBotClient _botClient = new();`
- In the existing `lobby_created` IPC handler (after backend mirroring and
  `_activeLobby = info`):
  - `var moddedPath = Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
       "AmongLauncher", "ModdedAmongUs");`
  - `var mod = LobbyTypeDetector.DetectLobbyType(moddedPath);`
  - `var roleId = mod == "modded" ? _config.ModdedRoleId : _config.VanillaRoleId;`
  - `var host = _config.UserName;` (Discord display name, set at login).
  - **Role ID validation (pre-send):** validate `roleId` against the Snowflake
    regex `/^\d{17,20}$/`. If `roleId` is empty or fails the regex, log
    `"Skipping lobby bot announce: invalid or missing role ID"` via
    `LauncherLog.Write(...)` and bypass sending the payload entirely. This
    prevents deterministic server-side payload rejections.
  - Build `LobbyBotPayload(info.Code, info.Region, host, mod, roleId, [])`
    (only after validation passes).
  - If `_config.BotWsEndpoint` is non-empty: call
    `SendLobbyCreatedAsync(payload)` which connects, sends, awaits the ACK,
    and logs the result. No keep-open connection is maintained across
    dispatch cycles (the bot closes after ACK).
- In the `lobby_closed` IPC handler: `_botClient.Disconnect();`.

### Data Flow

```
lobby_created (IPC from AmongAPI)
  └─ mirror to backend (existing)
  └─ LobbyTypeDetector.DetectLobbyType(moddedPath) → "modded" | "vanilla"
  └─ roleId = matching config field
  └─ validate roleId against /^\d{17,20}$/
       ├─ empty/invalid → LauncherLog "Skipping lobby bot announce: invalid or missing role ID", stop
       └─ valid → LobbyBotClient.SendLobbyCreatedAsync({ code, region, host, mod, roleId, appliedTags: [] })
            ├─ connect (one-shot) → send frame → ReceiveAsync → parse { ok, error } → log result
            └─ bot closes socket post-ACK → treated as normal completion, no reconnect

lobby_closed (IPC)
  └─ LobbyBotClient.Disconnect()
```

## Error Handling

- WS connect/send failures caught internally — never crash `lobby_created`.
- Empty `BotWsEndpoint` → skip bot integration silently.
- Missing modded install → `DetectLobbyType` returns `"vanilla"`.
- Empty or invalid `roleId` (fails `/^\d{17,20}$/`) → log warning and bypass
  sending the payload.
- Server socket closure after ACK → normal completion, not an error, no
  reconnect loop.
- Response parse failure (`ok`/`error` absent) → log the raw response text
  and continue; no crash.

## Testing

- Build must produce 0 errors (pre-existing warnings tolerated).
- Smoke test: launch app, open Settings, confirm the three new fields load and
  save to `config.json`.
- Manual: with a local WS echo server listening on `ws://127.0.0.1:8080`,
  trigger `lobby_created` and confirm the exact JSON payload arrives; confirm
  the ACK is read and logged, and the socket close after ACK ends the cycle
  without a reconnect storm; confirm a populated plugins dir yields `mod`
  `"modded"` and an empty/`AmongApi`-only dir yields `"vanilla"`; confirm an
  empty or non-numeric `roleId` logs
  "Skipping lobby bot announce: invalid or missing role ID" and sends nothing.
