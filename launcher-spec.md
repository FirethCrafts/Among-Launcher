# Launcher Specification

Technical specification of the **Among Launcher** WPF desktop application, the **Among API** BepInEx IL2CPP mod, and all communication protocols between them.

> **Transport note:** All local IPC uses **Windows Named Pipes** (`\\.\pipe\` namespace), not TCP. There are two separate pipes:
> - `AmongLauncher.IPC` — bidirectional control channel between launcher and in-game mod
> - `AmongLauncher.Redirect` — single-instance deep-link forwarding between launcher processes

---

## 1. Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Among Launcher (WPF)                      │
│  ┌─────────┐ ┌──────────┐ ┌──────────┐ ┌────────────────┐  │
│  │ Auth    │ │ Config   │ │ Game     │ │ IPC            │  │
│  │ (OAuth) │ │ (JSON)   │ │ Process  │ │ (PipeServer)   │  │
│  └─────────┘ └──────────┘ └──────────┘ └────────────────┘  │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ Lobby Services                                        │   │
│  │ BackendClient · WebSocket · Heartbeat · JoinPipeline  │   │
│  │ ModSetSync · ModCleanup · ProfileManager · BotClient  │   │
│  └──────────────────────────────────────────────────────┘   │
│  ┌──────────┐ ┌──────────┐ ┌──────────────────────────┐   │
│  │ Installer│ │ Steam/   │ │ Views                    │   │
│  │ BepInEx  │ │ Epic/    │ │ Main·Settings·Welcome·   │   │
│  │ GameCopy │ │ Xbox     │ │ HostPanel·Modals         │   │
│  └──────────┘ └──────────┘ └──────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
         │ Named Pipe (AmongLauncher.IPC)
         ▼
┌─────────────────────────────────────────────────────────────┐
│              Among API (BepInEx IL2CPP Plugin)               │
│  ┌──────────┐ ┌──────────────┐ ┌──────────────────────┐   │
│  │ PipeClient│ │ GameState    │ │ LobbyJoiner          │   │
│  │ (IPC)    │ │ Tracker      │ │ (main thread dispatch)│   │
│  └──────────┘ └──────────────┘ └──────────────────────┘   │
│  ┌──────────────────┐ ┌────────────────────────────────┐   │
│  │ GameAssembly     │ │ ChatCommandHandler             │   │
│  │ (Reflection)     │ │ /repost · /disband · /postlobby│   │
│  └──────────────────┘ └────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
         │ HTTP REST + WebSocket
         ▼
┌─────────────────────────────────┐
│  Python Backend (FastAPI)        │
│  Lobby store · Discord embeds    │
│  WebSocket hub · Mod storage     │
└─────────────────────────────────┘
```

### Component Dependencies

| Component | Depends On | Communicates Via |
|-----------|-----------|-----------------|
| Launcher → Among API | Named Pipe `AmongLauncher.IPC` | Length-prefixed JSON |
| Launcher → Backend | HTTP REST (`api/v1/`) | JSON + Bearer auth |
| Launcher → Bot | WebSocket (`BotWsEndpoint`) | JSON |
| Among API → Launcher | Named Pipe (client side) | Length-prefixed JSON |
| Among API → Backend | HTTP POST (direct, autopost) | JSON |
| Among API → Game | Reflection (IL2CPP) | Runtime type access |

---

## 2. Configuration

**File:** `%LocalAppData%\AmongLauncher\config.json`
**Persistence:** `LauncherConfig.Load()` / `LauncherConfig.Save()` — both swallow all exceptions.

### Fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Storefront` | `Storefront?` | `null` | Selected storefront (Steam/Epic/MicrosoftStore). Null = auto-detect. |
| `ServerUrl` | `string` | `"https://yourserver.com/api"` | Backend REST base URL. Launcher considers backend unconfigured if this contains `"yourserver.com"`. |
| `BackendWssUrl` | `string` | `"wss://yourserver.com/ws"` | Backend WebSocket URL. |
| `BotWsEndpoint` | `string` | `"ws://127.0.0.1:8080"` | Discord bot WebSocket endpoint. |
| `ModdedRoleId` | `string` | `""` | Discord role ID for modded lobby announcements. |
| `VanillaRoleId` | `string` | `""` | Discord role ID for vanilla lobby announcements. |
| `ModdedInstallPath` | `string` | `%LocalAppData%\AmongLauncher\ModdedAmongUs` | Path to the modded game copy. |
| `AvatarUrl` | `string` | `""` | Discord avatar URL (cached). |
| `UserName` | `string` | `""` | Discord username / display name (cached). |
| `DiscordAccessToken` | `string` | `""` | Discord OAuth2 bearer token. |
| `Profiles` | `List<ModProfile>` | `[]` | Saved mod profiles. |
| `Library` | `List<LibraryEntry>` | `[]` | Mod library entries. |
| `DebugMode` | `bool` | `false` | Enables debug join modal with detailed status. |
| `AutoPostLobby` | `bool` | `false` | Auto-creates lobby on backend when hosting. |

---

## 3. Deep-Links

### 3.1 URI Scheme Registration

Both schemes registered in `HKCU\Software\Classes\<scheme>` via `DeepLinkHandler.RegisterProtocol()`.

| Scheme | Purpose |
|--------|---------|
| `amongus-launcher` | Mod install |
| `amonglauncher` | Lobby join |

Shell command: `"<exe>" "%1"` — the launcher receives the URI as a process argument.

### 3.2 Mod Install Deep-Link

**Format:** `amongus-launcher://install?mods=<url1>,<url2>,<url3>`

- **`mods`:** Comma-separated list of URL-encoded absolute HTTP(S) URLs to mod DLLs.
- **Parsing:** `DeepLinkHandler.Parse()` splits on `,`, URL-decodes each, extracts filename from URL path.
- **Behavior:** Downloads each DLL into `BepInEx/plugins/` via `ModDownloader`, then launches the game.
- **Guard:** If modded install is not set up, shows a confirmation modal instead of downloading.

### 3.3 Lobby Join Deep-Link

**Format:** `amonglauncher://join?code=ABCDEF`

- **`code`:** 4–8 character alphanumeric lobby code. Trimmed, uppercased, URL-decoded, validated against regex `^[A-Za-z0-9]{4,8}$`.
- **Parsing:** `DeepLinkHandler.TryParseJoin()` handles both query param (`?code=X`) and path (`/X`) formats.
- **Full join pipeline** (`HandleJoinLinkAsync` at `MainWindow.xaml.cs:213`):
  1. Refresh config from disk
  2. Validate backend is configured (ServerUrl not containing `yourserver.com`)
  3. `GET /api/v1/lobbies/{code}` — fetch lobby from backend
  4. Verify modded install exists (`winhttp.dll` present)
  5. `ModSetSync.DiffAsync()` — diff lobby's mod set against local plugins
  6. `ModSetSync.InstallAsync()` — download missing DLLs (with SHA-256 verification)
  7. `ModCleanupEngine.QuarantineAsync()` — move non-required mods to `.disabled/`
  8. Kill any running game
  9. Launch Among Us with launch args (including `--server-url`, `--autopost`/`--no-autopost`)
  10. `WaitForGameReadyAsync()` — up to 90s timeout, racing TCS + process exit
  11. Broadcast `join_lobby` over IPC pipe: `{ code, region, regionIp, regionPort }`
  12. `LobbyWebSocketClient.ConnectAsync()` — connect to backend WebSocket
  13. Return join outcome

### 3.4 Debug Mode Join

When `DebugMode` is enabled:
- A `JoinDebugModal` is shown instead of auto-launching
- Displays real-time status: lobby info, mod sync progress, errors
- Green PLAY button appears when everything is ready
- Clicking PLAY starts the game, waits for `game_ready`, sends `join_lobby`

---

## 4. Single-Instance Routing

**Mutex:** `Global\AmongLauncher.SingleInstance`

- Primary instance creates the global mutex and starts `SingleInstance.StartRedirectServer()`.
- Redirect pipe: `AmongLauncher.Redirect` — listens for one UTF-8 line per connection (the raw deep-link string).
- Secondary instances call `SingleInstance.ForwardDeepLink(deepLink)`: connect write-only to the redirect pipe (2s timeout), write the deep-link as a single line, then exit.
- On the primary, the received link fires `App.DeepLinkReceived`, dispatched to `MainWindow.HandleDeepLink()`.

---

## 5. Discord OAuth2 Authentication

**Service:** `DiscordAuthService`

### Constants

| Constant | Value |
|----------|-------|
| `ClientId` | `1533706803748147240` |
| `ClientSecret` | `Um7wPIDVkCS9ro-0ZltYrs1NUI2q2LLh` |
| `RedirectUri` | `http://localhost:5000/callback/` |
| `AuthorizeUrl` | `https://discord.com/oauth2/authorize?client_id=...&response_type=code&redirect_uri=...&scope=identify` |
| Scopes | `identify` |

### OAuth Flow

1. `LoginAsync()` starts `HttpListener` on `localhost:5000`
2. Opens browser to Discord OAuth2 authorize URL
3. Waits for callback (up to 5 minutes)
4. Exchanges authorization code for access token via `POST https://discord.com/api/v10/oauth2/token`
5. Fetches user profile via `GET https://discord.com/api/v10/users/@me` (Bearer token)
6. Returns `DiscordUserProfile(Id, Username, GlobalName, AvatarUrl)`
7. Launcher saves avatar URL, username, and token to config
8. Avatar displayed in sidebar as circle-clipped image

---

## 6. Game Detection & Installation

### 6.1 Storefront Detection

**Enum:** `Storefront { Steam, Epic, MicrosoftStore }`

**Steam** (`SteamFinder.cs`):
- Registry: `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`
- Parses `libraryfolders.vdf` for additional Steam library paths
- Searches each library for `steamapps\common\Among Us\Among Us.exe`
- Fallback paths: `C:\Program Files (x86)\Steam`, `D:\SteamLibrary`, etc.

**Epic** (`GameFinder.cs`):
- Parses `*.item` manifest files in `ProgramData\Epic\EpicGamesLauncher\Data\Manifests`
- Checks `GameUserSettings.ini` in `%LocalAppData%\Among Us\Game\Settings`
- Fallback paths: `C:\Program Files\Epic Games\Among Us`, etc.

**Microsoft Store / Xbox** (`GameFinder.cs`):
- Scans all fixed drives for `Among Us\Among Us.exe` or `Among Us\Content\Among Us.exe`
- Checks `Program Files\WindowsApps\Innersloth*`
- Checks `%LocalAppData%\Packages\InnerSloth.LLC-*`

**Auto-detection:** Tries Steam → Epic → Xbox in order, returns first found.

### 6.2 Installation Flow

1. **Copy game:** `GameCopier.CopyGameAsync()` copies all files (except `.pdb`) from vanilla to `%LocalAppData%\AmongLauncher\ModdedAmongUs`
2. **Install BepInEx:** `BepInExInstaller.InstallAsync()` — local dir or download from GitHub releases; different builds for Steam vs Epic/MS Store
3. **Write `steam_appid.txt`:** for Steam (app ID `945360`)
4. **Install AmongApi.dll:** Downloads from `https://github.com/FirethCrafts/Among-Launcher/releases/latest/download/AmongApi.dll`

---

## 7. Game Process Management

**Service:** `GameProcessManager`

| Method | Behavior |
|--------|----------|
| `LaunchGame(exePath, arguments?)` | Starts `Process`, hooks `Exited` event |
| `KillGame()` | `CloseMainWindow()` → wait 15s → `Kill()` → wait 15s |
| `IsGameRunning()` | Returns `!HasExited` |

**Event:** `GameExited`

### Launch Arguments

| Argument | Condition |
|----------|-----------|
| `-EpicPortal` | Epic storefront |
| `--autopost` | `AutoPostLobby` is true |
| `--no-autopost` | `AutoPostLobby` is false |
| `--server-url=<url>` | Always sent (from `config.ServerUrl`) |

---

## 8. Mod Management

### 8.1 Installed Mods

Scans `BepInEx/plugins/*.dll` → returns `List<ModInfo>` with name, description, and file path.

### 8.2 Import Local Mod

File dialog → copies selected `.dll` to `BepInEx/plugins/` with overwrite-conflict handling.

### 8.3 GitHub Preset Mods

`PresetModLibraryModal` hardcoded presets:

| Name | Repository | Preferred Asset |
|------|-----------|----------------|
| EHR (Endless Host Roles) | `Gurge44/EndlessHostRoles` | `EHR.dll` |
| AUnlocker | `astra1dev/AUnlocker` | (any `.dll`) |
| Town of Us Mira | `AU-Avengers/TOU-Mira` | `TownOfUsMira.dll` |
| Town of Us Reactivated | `badzyn/TOU-Mira` | `TownOfUsMira.dll` |
| Lotus | `Lotus-AU/LotusContinued` | `Lotus.dll` |

**Install flow:** Fetch GitHub releases API → find `.dll` asset → download via `ModDownloader`.

### 8.4 Mod Downloader

`ModDownloader.DownloadToFileAsync()`: skips if file exists and non-empty; retries on `IOException` with backoff `[250, 500, 1000, 2000, 4000]` ms.

### 8.5 SHA-256 Verification

`Sha256Helper.HashFileAsync()` — lowercase hex SHA-256. Used during mod-set sync.

---

## 9. Mod Profiles

**Service:** `ModProfileManager`

```csharp
class ModProfile {
    string Name;
    List<ModSetEntry> Mods; // FileName, DownloadUrl, Sha256?, Version?
}
```

| Operation | Behavior |
|-----------|----------|
| `LoadProfiles()` | Returns `_config.Profiles` |
| `SaveProfile(name, mods)` | Upserts profile by name, saves config |

**Apply profile:** Diff against installed → download missing → move extras to library.

---

## 10. Mod Library

**Service:** `LibraryManager`
**Library dir:** `%LocalAppData%\AmongLauncher\Library`

| Operation | Behavior |
|-----------|----------|
| `LoadLibrary()` | Returns config entries, prunes missing files |
| `AddToLibrary(sourceFilePath, downloadUrl?, version?)` | Copies DLL to library, records in config |
| `InstallToPlugins(fileName, pluginsDir)` | Copies from library to plugins |
| `RemoveFromLibrary(fileName)` | Deletes file + config entry |
| `MoveNonListedToLibrary(pluginsDir, keepFileNames)` | Moves non-listed DLLs to library |

---

## 11. Mod Cleanup

**Service:** `ModCleanupEngine`

**Whitelist** (never quarantined): `AmongApi.dll`, `AUnlocker.dll`, `helper_mod.dll`, `aunlocker`

**Behavior:** `QuarantineAsync(requiredFileNames, ct)` moves non-whitelisted, non-required DLLs/dirs to `BepInEx/plugins/.disabled/`. Never hard-deleted.

---

## 12. Named Pipe IPC Protocol

### 12.1 Pipe Server

| Setting | Value |
|---------|-------|
| Pipe name | `AmongLauncher.IPC` |
| Direction | `InOut` (bidirectional) |
| Max instances | `1` |
| Transmission mode | `Byte` |
| Options | `Asynchronous` |

Accepts one client at a time. Started in `MainWindow` constructor.

### 12.2 Wire Format

```
[ 4 bytes: payload length (int32, little-endian) ][ N bytes: JSON payload ]
```

Max payload: **1 MB**. `ReadMessage` validates `0 < len ≤ 1 MB`.

### 12.3 JSON Message Envelope

```json
{
  "type": "<message_type>",
  "id": "<8_char_hex>",
  "timestamp": 1735689600000,
  "payload": { ... }
}
```

**Response asymmetry:**
- Mod → Launcher: mod sends with `id`/`timestamp`, launcher serializes handler return directly (no echo).
- Launcher → Mod: launcher sends with `id`/`timestamp`, mod echoes `id` in `<type>_ack`.

### 12.4 Message Types

#### Launcher → Among API

| Type | Payload | Description |
|------|---------|-------------|
| `launcher_ready` | _(none)_ | Handshake on window load |
| `set_server_url` | `{ url }` | Sends backend URL to plugin |
| `join_lobby` | `{ code, region, regionIp, regionPort }` | Ask plugin to join lobby |

#### Among API → Launcher

| Type | Payload | Response |
|------|---------|----------|
| `game_ready` | _(none)_ | `{ type: "game_ready_ack", restart: false }` |
| `lobby_created` | `{ code, region, regionIp, regionPort, host, playerCount, maxPlayers }` | `{ type: "lobby_created_ack" }` |
| `lobby_closed` | `{ code, reason }` | `{ type: "lobby_closed_ack" }` |
| `player_joined` | `{ playerName, playerCount }` | _(none)_ |
| `player_left` | `{ playerName, playerCount }` | _(none)_ |
| `join_lobby_result` | `{ success, error? }` | _(none)_ |

### 12.5 Launcher Handler Details

#### `lobby_created`

1. Reads IPC payload: `code`, `region`, `regionIp`, `regionPort`, `host`, `playerCount`, `maxPlayers`
2. Resolves host name: `_config.UserName` > IPC `host` (if not "UNKNOWN") > `"Host"`
3. Builds `LobbyInfo` with `HostUserId = _userId`, `MaxPlayers = maxPlayers`
4. Scans installed mods via `GetInstalledModSetAsync()`
5. Uploads each mod DLL to backend via `POST /api/v1/mods`
6. If `AutoPostLobby`: `POST /api/v1/lobbies` → immediate heartbeat → start heartbeat service (30s) → connect WebSocket
7. Shows `HostControlPanelView` if user is host
8. Detects lobby type via `LobbyTypeDetector`
9. Sends bot announcement via `LobbyBotClient`

#### `lobby_closed`

`DELETE /api/v1/lobbies/{code}` → stop heartbeat → disconnect WebSocket → disconnect bot → clear `_activeLobby`.

---

## 13. Among API Plugin

### 13.1 Plugin Lifecycle

1. **Load:** Parse CLI args (`--autopost`, `--no-autopost`, `--server-url=`)
2. **RunAsync:** Wait for game init (up to 30s, 500ms polling), connect `PipeClient`
3. Send `game_ready`
4. Start `GameStateTracker` (500ms polling)
5. Register handlers: `set_server_url`, `join_lobby`
6. Start `ChatCommandHandler` (500ms polling)
7. On disconnect: stop tracker, stop commands, dispose joiner

### 13.2 GameAssembly Reflection Bridge

Lazy, cached reflection over `Assembly-CSharp` IL2CPP interop. Zero compile-time game references.

**Assembly resolution chain:** Loaded assemblies → `BepInEx/interop/Assembly-CSharp.dll` → All loaded assemblies → `Il2CppInterop.Runtime.dll`

**Key methods:** `Type()`, `GetStaticProp()`, `GetInstanceProp()`, `GetInstanceMember()`, `GetStaticMember()`, `CallStaticMethod()`, `CallInstanceMethod()`, `InLobby()`, `AmongUsClient()`, `CurrentRegionName()`, `LocalPlayerName()`

**Error handling:** All failures → `null` + log; never throw.

### 13.3 Game State Tracker

500ms polling. State machine:

| Transition | Condition | Event |
|-----------|-----------|-------|
| Not in → In lobby | `IsHost()` | `LobbyCreated` |
| In → Not in lobby | Was host | `LobbyClosed` |
| Player count ↑ | In lobby | `PlayerJoined` |
| Player count ↓ | In lobby | `PlayerLeft` |

**Host detection:** `AmHost` property → `InnerNetClient.AmHost` static → `HostId == CurrentClient` fallback.

**Max players:** `AmongUsClient.GameHostOpts.MaxPlayers` or `NormalOptions.MaxPlayers`, default 15.

### 13.4 Lobby Joiner

Background queue pump. Dispatches to Unity main thread via `SynchronizationContext.Post`.

**Join sequence:**
1. Construct `ServerInfo` + `StaticHttpRegionInfo` for custom regions
2. `GameCode.GameNameToInt(code)`
3. `AmongUsClient.CoJoinOnlineGameFromCode(gameId, false)`
4. `AmongUsClient.StartCoroutine(enumerator)`

Timeouts: 30s dispatch, 15s main-thread completion.

### 13.5 Chat Commands

| Command | Action | IPC |
|---------|--------|-----|
| `/repost` | Resend lobby to backend | `lobby_created` |
| `/disband` | Leave lobby + notify backend | `lobby_closed` (reason: `"disband"`) |
| `/postlobby` | Post lobby to backend | _(direct HTTP)_ |

### 13.6 Auto-Post

When `--autopost`: plugin calls `PostLobbyToBackend()` directly on lobby creation.
`POST {serverUrl}/api/v1/lobbies` with `{ code, region, host, mod_type: "modded", mods: [], max_players }`.

### 13.7 Lobby Leave

`AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame)` via reflection.

### 13.8 Logging

**File:** `BepInEx/AmongApi.log`
**Format:** `[yyyy-MM-dd HH:mm:ss] [LEVEL] message`

---

## 14. Lobby Lifecycle

### Host Creates Lobby

```
1. Player enters lobby in-game (host detected by GameStateTracker)
2. Plugin fires LobbyCreated event
3. Plugin sends "lobby_created" IPC to launcher
4. Plugin optionally calls PostLobbyToBackend() (autopost)
5. Launcher receives "lobby_created":
   a. Resolves host name
   b. Scans installed mods
   c. Uploads mods to backend
   d. Creates lobby on backend (POST /api/v1/lobbies)
   e. Sends immediate heartbeat
   f. Starts heartbeat service (every 30s)
   g. Connects WebSocket
   h. Shows host control panel
   i. Sends bot announcement
```

### Guest Joins Lobby

```
1. Deep link amonglauncher://join?code=X received
2. Launcher fetches lobby from backend (GET /api/v1/lobbies/{code})
3. Launcher diffs mod set against local plugins
4. Downloads missing mods (SHA-256 verified)
5. Quarantines extra mods to .disabled/
6. Kills running game
7. Launches Among Us with --server-url and --no-autopost
8. Waits up to 90s for "game_ready" from plugin
9. Sends "join_lobby" IPC with { code, region, regionIp, regionPort }
10. Plugin joins in-game via reflection
11. Plugin sends "join_lobby_result"
12. Launcher connects WebSocket
```

### Host Disbands Lobby

```
1. Host clicks Disband or types /disband
2. Confirmation modal shown (if UI)
3. DELETE /api/v1/lobbies/{code}
4. Stop heartbeat service
5. Disconnect WebSocket
6. Disconnect bot client
7. Plugin calls AmongUsClient.ExitGame()
8. Clear _activeLobby
```

---

## 15. Lobby Backend REST API

**Base URL:** `config.ServerUrl` (default `https://yourserver.com/api`)
**Auth:** `Authorization: Bearer <DiscordAccessToken>` when token is non-empty.
**Timeout:** 8 seconds per request.

| Method | Endpoint | Body | Response |
|--------|----------|------|----------|
| `POST` | `api/v1/lobbies` | `{ code, region, host, mod_type, mods[], max_players }` | `bool` (success) |
| `GET` | `api/v1/lobbies/{code}` | — | `LobbyResponse` → `LobbyInfo` |
| `POST` | `api/v1/lobbies/{code}/heartbeat` | — | `bool` |
| `POST` | `api/v1/lobbies/{code}/repost` | — | `bool` |
| `POST` | `api/v1/lobbies/{code}/kick` | `{ player_id }` | `bool` |
| `DELETE` | `api/v1/lobbies/{code}` | — | `bool` |
| `POST` | `api/v1/mods` | `multipart/form-data` (file, name) | `ModInfoEntry?` |
| `GET` | `api/v1/mods/{id}/download` | — | Binary file |

### Data Shapes

**CreateLobbyRequest:**
```json
{
  "code": "ABCD",
  "region": "NA",
  "host": "Alice",
  "mod_type": "modded",
  "mods": [{"name": "ExampleMod", "version": "1.0.0", "file_hash": "9f86d08..."}],
  "max_players": 15
}
```

**LobbyResponse:**
```json
{
  "code": "ABCD",
  "region": "NA",
  "host": "Alice",
  "mod_type": "modded",
  "mods": [{"name": "ExampleMod", "version": "1.0.0", "file_hash": "..."}],
  "players": [{"id": "host", "name": "Alice", "is_host": true}],
  "max_players": 15
}
```

---

## 16. Lobby WebSocket

**Endpoint:** `config.BackendWssUrl?code={lobbyCode}`
**Auth:** `Authorization: Bearer <DiscordAccessToken>` header.
**Reconnect:** Exponential backoff, 2s × min(5, attempt).

### Incoming Messages

| Action | Payload | Launcher Behavior |
|--------|---------|-------------------|
| `kick` | `{ reason }` | Kill the game |
| `rejoin` | `{ lobbyCode, modSet, region, regionIp, regionPort }` | Install new mod set, relaunch, rejoin |

---

## 17. Heartbeat System

**Service:** `LobbyHeartbeatService`

- Sends `POST /api/v1/lobbies/{code}/heartbeat` every **30 seconds**
- No request body
- Immediate heartbeat sent after lobby creation (before 30s interval starts)
- Stopped on: lobby close, disband, or kick
- Backend auto-expires lobbies after grace period (default 90s) without heartbeat

---

## 18. Discord Bot Integration

**Client:** `LobbyBotClient`
**Endpoint:** `config.BotWsEndpoint` (default `ws://127.0.0.1:8080`)

**Lobby creation payload:**
```json
{
  "code": "ABCD",
  "region": "NA",
  "host": "Alice",
  "mod": "modded",
  "role_id": "1234567890",
  "applied_tags": []
}
```

Lobby type detected by `LobbyTypeDetector`: scans plugins for non-excluded DLLs → `"modded"` or `"vanilla"`. Excluded: `AmongApi.dll`, `0Harmony.dll`, `AsmResolver.dll`, `BepInEx.*.dll`.

---

## 19. UI Architecture

### Views

| View | Purpose |
|------|---------|
| `WelcomeView` | Login screen with Discord OAuth button, ambient bloom animation |
| `MainView` | Game status, mod list, install/play buttons, profiles, mod import |
| `SettingsView` | Game path, server URL, bot endpoint, role IDs, debug/auto-post toggles |
| `LibraryView` | Mod library with install/remove actions |
| `HostControlPanelView` | Live player list, lobby code, region, repost/kick/disband |

### Modals

| Modal | Purpose |
|-------|---------|
| `ConfirmationModal` | Generic confirm/cancel with danger option |
| `DownloadModsModal` | Sequential mod downloads with progress bars |
| `JoinDebugModal` | Real-time join status with PLAY button |
| `LogViewerModal` | IPC log viewer with copy/refresh/clear |
| `PresetModLibraryModal` | GitHub preset mod library |
| `MsStoreAccessModal` | MS Store/Epic permission guidance |
| `StorefrontPickerModal` | Multiple install picker |
| `LibraryPickerModal` | Library mod picker |

### Navigation

Sidebar with Home, Library, Lobby (hidden when no lobby), Settings, Logout. Discord avatar in sidebar. Content swapped via `ContentControl`.

### Title Bar

Custom borderless with `WindowChrome`. Status badge: red indicator + "No Game Running" / "Among Us — Running" + STOP button.

---

## 20. Theming & Accessibility

### Color Palette

| Key | Value | Usage |
|-----|-------|-------|
| `AmbientBgColor` | `#0B0B0E` | Window background |
| `GlassSurfaceColor` | `#C9151518` | Card backgrounds |
| `GlassSurfaceStrongColor` | `#E6151518` | Stronger glass |
| `GlassSurfaceWeakColor` | `#A6151518` | Weaker glass |
| `GlassBorderColor` | `#2EFFFFFF` | Card borders |
| `AccentColor` | `#5865F2` | Discord blurple |
| `PlayColor` | `#10B981` | Green (play/success) |
| `StopColor` | `#DC2626` | Red (stop/danger) |

### Animations

- `HoverEnterGlow` / `HoverExitGlow` — DropShadowEffect glow on hover
- `PressScale` / `ReleaseScale` — ScaleTransform on press
- Bloom breathe on welcome screen (80s/90s oscillation)
- Modal scale/translate entrance

### Accessibility

`ReduceMotion` — reads `SystemParameters.ClientAreaAnimation`. When true, skips all storyboard animations (bloom, hover glow, press scale, modal entrance).
