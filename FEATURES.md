# Features

Complete feature documentation for the **Among Launcher** (WPF desktop app) and **Among API** (BepInEx IL2CPP mod DLL running inside Among Us).

---

## Among Launcher (WPF Desktop App)

### Game Detection & Setup

- **Multi-storefront detection:** Automatically finds Among Us across Steam, Epic Games, and Microsoft Store / Xbox.
  - **Steam:** Reads `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`, parses `libraryfolders.vdf` for additional library paths, searches each for `steamapps\common\Among Us\Among Us.exe`. Includes fallback paths (`C:\Program Files (x86)\Steam`, `D:\SteamLibrary`, etc.).
  - **Epic:** Parses `*.item` manifest files in `ProgramData\Epic\EpicGamesLauncher\Data\Manifests`, checks `GameUserSettings.ini` in `%LocalAppData%\Among Us\Game\Settings`, falls back to common install paths.
  - **Microsoft Store / Xbox:** Scans all fixed drives for `Among Us\Among Us.exe` or `Among Us\Content\Among Us.exe`, checks `Program Files\WindowsApps\Innersloth*` and `%LocalAppData%\Packages\InnerSloth.LLC-*`.
- **Auto-detection fallback chain:** Steam → Epic → Xbox, returns first found.
- **Settings UI:** Storefront picker combo (Steam/Epic/MS Store/Auto), auto-scan button, manual folder browse with storefront auto-matching.
- **Storefront picker modal:** When multiple installs are found, shows a clickable list for the user to choose.
- **One-click setup:** Copies the game to `%LocalAppData%\AmongLauncher\ModdedAmongUs`, installs BepInEx 6 IL2CPP (storefront-specific build from bundled files or GitHub release), writes `steam_appid.txt` for Steam, installs `AmongApi.dll`, all with progress reporting.
- **Reset install option:** Deletes the entire modded copy directory.

### Discord Authentication & User Profile

- **Discord OAuth2** (`identify` scope) via a local HTTP callback listener on `localhost:5000`.
- **Login flow:** Starts `HttpListener` → opens browser to Discord authorize URL → waits for callback (5-minute timeout) → exchanges authorization code for access token → fetches user profile (`/api/v10/users/@me`).
- **User data:** Fetches user ID, username, global name, and avatar URL; persists all to config.
- **Avatar display:** Shows Discord avatar as a circle-clipped image in the sidebar.
- **Logout:** Confirmation modal → clears avatar URL → returns to welcome screen.

### Game Management

- **Launch/stop:** Starts the modded Among Us executable via `GameProcessManager`; running-status indicator updates the UI title bar badge (red/gray).
- **PLAY/STOP toggle:** Button switches between play and stop states based on game running status.
- **Storefront-aware launch args:** Adds `-EpicPortal` for Epic, `--autopost`/`--no-autopost` for lobby auto-posting, `--server-url=` for backend URL.
- **Browse files:** Opens the modded install folder in Windows Explorer.
- **Game exit detection:** Hooks `Process.Exited` event to update UI when the game closes.

### Mod Management

- **Installed mods list:** Scans `BepInEx/plugins/*.dll` and displays each mod with its name and file size.
- **Import local DLL:** File dialog to select a `.dll`, copies it into the plugins folder with overwrite-conflict handling.
- **Remove mod:** Danger confirmation modal, then deletes the DLL from plugins.
- **To library:** Copies a mod from plugins into the persistent library folder.
- **GitHub preset mods:** Hardcoded preset library with one-click install from GitHub releases:
  | Name | Repository | Preferred Asset |
  |------|-----------|----------------|
  | EHR (Endless Host Roles) | `Gurge44/EndlessHostRoles` | `EHR.dll` |
  | AUnlocker | `astra1dev/AUnlocker` | (any `.dll`) |
  | Town of Us Mira | `AU-Avengers/TOU-Mira` | `TownOfUsMira.dll` |
  | Town of Us Reactivated | `badzyn/TOU-Mira` | `TownOfUsMira.dll` |
  | Lotus | `Lotus-AU/LotusContinued` | `Lotus.dll` |
- **Mod downloader:** Sequential download with progress bars, retries on `IOException` with exponential backoff (`250 → 500 → 1000 → 2000 → 4000` ms), skips if file exists.
- **SHA-256 verification:** Downloads are verified against the backend's recorded hash; mismatched files are deleted.

### Mod Profiles

- **Save profile:** Name a mod set and save it as a reusable preset. Stored in `config.json` as `List<ModProfile>`.
- **Apply profile:** Diffs the profile's mod set against installed plugins, downloads any missing DLLs, moves non-profile mods to the library.
- **Profile selector:** ComboBox in `MainView` to switch between saved profiles.

### Mod Library

- **Persistent storage:** `%LocalAppData%\AmongLauncher\Library` — mods survive profile switches and cleanup.
- **Add to library:** Copies a DLL from plugins into the library folder, records metadata (filename, download URL, version) in config.
- **Install from library:** Copies a library mod back into `BepInEx/plugins/`.
- **Remove from library:** Deletes the file from disk and removes the config entry.
- **Pruning:** `LoadLibrary()` removes entries whose files no longer exist on disk.
- **Auto-move:** `MoveNonListedToLibrary()` moves DLLs not in a given keep-list to the library (used during profile apply).

### Mod Cleanup (Quarantine)

- **Quarantine engine:** Moves mods not in the lobby's required set to `BepInEx/plugins/.disabled/` — never hard-deleted.
- **Whitelist** (never quarantined): `AmongApi.dll`, `AUnlocker.dll`, `helper_mod.dll`, `aunlocker`.
- **Triggered:** Before joining a lobby via deep link, to ensure only required mods are active.

### Lobby Backend Integration

- **REST client** (`LobbyBackendClient`) communicating with the Python FastAPI backend at `api/v1/` with optional Bearer token auth.
- **Endpoints:**
  | Method | Endpoint | Purpose |
  |--------|----------|---------|
  | `POST` | `api/v1/lobbies` | Create/refresh lobby |
  | `GET` | `api/v1/lobbies/{code}` | Fetch lobby for mod-set sync |
  | `POST` | `api/v1/lobbies/{code}/heartbeat` | Keepalive (every 30s) |
  | `POST` | `api/v1/lobbies/{code}/repost` | Refresh Discord embed |
  | `POST` | `api/v1/lobbies/{code}/kick` | Push kick to connected launchers |
  | `DELETE` | `api/v1/lobbies/{code}` | Disband lobby |
  | `POST` | `api/v1/mods` | Upload mod DLL (multipart) |
  | `GET` | `api/v1/mods/{id}/download` | Download mod DLL |
- **Lobby data sent:** `code`, `region`, `host` (player name), `mod_type` (`"modded"`), `mods[]` with `name`/`version`/`file_hash`, `max_players`.
- **WebSocket client** (`LobbyWebSocketClient`) with reconnect/backoff that receives live `kick` and `rejoin` commands from the backend.
- **Host control panel:** Live player list (ObservableCollection), repost, kick, and disband controls; disband gated by a confirmation modal.
- **Heartbeat service:** Sends `POST /api/v1/lobbies/{code}/heartbeat` every 30 seconds with no body. An immediate heartbeat is sent right after lobby creation to prevent backend expiry before the first interval fires.
- **Mod-set sync:** Diffs the lobby's mod set against local `BepInEx/plugins/`, downloads any missing DLLs with SHA-256 verification, quarantines extras.
- **In-game chat commands:** `/repost` and `/disband` from the mod surface into the launcher's host flow via IPC.
- **Max players:** Sent as `max_players` in lobby creation requests; read from the game via `AmongUsClient.GameHostOpts.MaxPlayers` or `NormalOptions.MaxPlayers` (default 15).

### Deep-Link Lobby Join

- **URI:** `amonglauncher://join?code=XXXXXX`
- **Full pipeline:**
  1. Fetch lobby from backend (`GET /api/v1/lobbies/{code}`)
  2. Verify modded install exists (`winhttp.dll`)
  3. Diff mod set against local plugins
  4. Download missing mods (SHA-256 verified)
  5. Quarantine extra mods to `.disabled/`
  6. Kill running game
  7. Launch Among Us with `--server-url` and `--no-autopost`
  8. Wait up to 90s for `game_ready` from the in-game mod
  9. Send `join_lobby` IPC message with lobby details
  10. Connect WebSocket for live updates
- **Single-instance routing:** A second instance forwards the deep link to the already-running primary via the `AmongLauncher.Redirect` named pipe, then exits.
- **Mod install deep-link:** `amongus-launcher://install?mods=<url1>,<url2>` — downloads and installs mods, then launches the game.

### Discord Bot Integration

- **Lobby creation events:** Sends lobby details to a Discord bot via WebSocket for forum thread creation.
- **Lobby type detection:** Scans plugins for non-excluded DLLs → `"modded"` or `"vanilla"` (excluded: `AmongApi.dll`, `0Harmony.dll`, `AsmResolver.dll`, `BepInEx.*.dll`).
- **Role selection:** Uses configured `ModdedRoleId` or `VanillaRoleId` based on detected lobby type.

### IPC & Infrastructure

- **Named-pipe server** (`AmongLauncher.IPC`) for bidirectional JSON messaging with the in-game mod; connection status shown in the UI.
- **Named-pipe redirect server** (`AmongLauncher.Redirect`) for single-instance deep-link forwarding.
- **IPC log viewer modal:** Reads `%LocalAppData%\AmongLauncher\AmongLauncher_ipc.log`, with copy/refresh/clear actions.
- **Persistence:** `config.json` stores storefront, server URLs, Discord token/avatar/username, profiles, library, bot WS endpoint, role IDs, debug mode, auto-post toggle.
- **Config reload:** Re-read from disk at the start of join pipeline to pick up settings changes.

---

## Among API (BepInEx IL2CPP Mod DLL)

### IPC Client

- **Named-pipe client** that connects to `AmongLauncher.IPC` with retry (5 attempts, 10s timeout each, 2s delay between).
- Sends length-prefixed JSON frames with `type`, `id` (8-char GUID), `timestamp`, and optional `payload`.
- Registers request handlers and sends `<type>_ack` responses echoing the request `id`.
- **Graceful disconnect:** When the launcher isn't running, the mod loads normally without IPC.
- **Write serialization:** `SemaphoreSlim` ensures only one frame is written at a time.

### Reflection Bridge (GameAssembly)

- **Lazy, cached reflection** over the game's `Assembly-CSharp` IL2CPP interop assembly.
- Resolves types and members at runtime with zero compile-time game-assembly references.
- **Assembly resolution chain:** Loaded assemblies → `BepInEx/interop/Assembly-CSharp.dll` → All loaded assemblies → `Il2CppInterop.Runtime.dll`.
- **Type/member caching:** `ConcurrentDictionary` — single resolution per key, then cached forever.
- **Key methods:** `Type()`, `GetStaticProp()`, `GetInstanceProp()`, `GetInstanceMember()`, `GetStaticMember()`, `CallStaticMethod()`, `CallInstanceMethod()`, `CreateInstance()`, `GenericType()`, `EnumValue()`.
- **Game state helpers:** `InLobby()`, `AmongUsClient()`, `CurrentRegionName()`, `LocalPlayerName()`.
- **Player name resolution:** Multi-fallback: `PlayerControl.LocalPlayer.Data.PlayerName` → `PlayerControl.LocalPlayer.PlayerName` → `GameData.Instance.AllPlayers` (find by `OwnerId`).
- **Error handling:** All failures degrade to `null` + log entry; never throw.

### Game State Tracking

- **500ms polling loop** (`GameStateTracker`) that detects lobby enter/leave and player-count changes.
- **State machine transitions:**
  | From | To | Condition | Event |
  |------|----|-----------|-------|
  | Not in lobby | In lobby | `IsHost()` is true | `LobbyCreated` |
  | In lobby | Not in lobby | Was host | `LobbyClosed` |
  | — | — | Player count increased | `PlayerJoined` |
  | — | — | Player count decreased | `PlayerLeft` |
- **Host detection:** `AmHost` instance property → `InnerNetClient.AmHost` static → `HostId == CurrentClient` fallback.
- **Lobby code:** `GameCode.IntToGameName(AmongUsClient.GameId)`.
- **Player count:** `GameData.Instance.PlayerCount`.
- **Max players:** `AmongUsClient.GameHostOpts.MaxPlayers` or `NormalOptions.MaxPlayers`, default 15.
- **Events sent to launcher:** `lobby_created` (with code, region, host, playerCount, maxPlayers), `lobby_closed` (with code, reason), `player_joined`, `player_left`.

### Direct In-Game Lobby Join

- **Background queue pump** (`LobbyJoiner`) that dispatches join operations to the Unity main thread via `SynchronizationContext.Post`.
- **Join sequence:**
  1. Get `ServerManager` instance via reflection
  2. For custom regions: construct `ServerInfo` + `StaticHttpRegionInfo`, call `AddOrUpdateRegion` + `SetRegion`
  3. Decode lobby code: `GameCode.GameNameToInt(code)`
  4. Start `AmongUsClient.CoJoinOnlineGameFromCode(gameId, false)` coroutine
  5. `AmongUsClient.StartCoroutine(enumerator)`
- **Timeouts:** 30s for dispatch to main thread, 15s for main-thread completion.
- **Result:** Returns `JoinResult(Success, Error?)` to the launcher via `join_lobby_result` IPC message.

### In-Game Chat Commands

- **500ms poll** of `HudManager.Chat.freeChatField.Text` via `ChatCommandHandler`.
- **Commands:**
  | Command | Action | IPC |
  |---------|--------|-----|
  | `/repost` | Resend lobby to backend | Resends `lobby_created` |
  | `/disband` | Leave lobby + notify backend | Sends `lobby_closed` (reason: `"disband"`) + calls `ExitGame()` |
  | `/postlobby` | Post lobby to backend | Direct HTTP POST |
- Input is cleared after command detection to prevent commands from being sent as chat.

### Auto-Post

- When `--autopost` is passed as a launch argument, the plugin directly calls `PostLobbyToBackend()` on lobby creation.
- `POST {serverUrl}/api/v1/lobbies` with `{ code, region, host, mod_type: "modded", mods: [], max_players }`.
- Timeout: 15 seconds.
- Host name resolved with fallback: raw IPC host → `"Host"` if "UNKNOWN".

### Lobby Leave

- `AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame)` via reflection.
- Called by the `/disband` chat command.

### Logging

- **File:** `BepInEx/AmongApi.log`
- **Format:** `[yyyy-MM-dd HH:mm:ss] [LEVEL] message`
- **Levels:** INFO, WARN, ERROR
- Thread-safe append with lock. Also logs to BepInEx console.

---

## Cross-Cutting Features

### Single-Instance Enforcement

- Global mutex `Global\AmongLauncher.SingleInstance` ensures only one launcher runs.
- Secondary instances forward deep links via the `AmongLauncher.Redirect` named pipe and exit.

### Error Resilience

- All config load/save operations swallow exceptions (corrupted config → defaults).
- IPC handler failures are caught and logged without crashing.
- Mod download retries with exponential backoff.
- WebSocket reconnects with backoff on drop.
- Game process kill has 15s timeout at each stage (close → kill).

### Accessibility

- `ReduceMotion` flag reads `SystemParameters.ClientAreaAnimation`.
- When true, skips all storyboard animations: bloom breathe, hover glow, press scale, modal entrance.

### Theming

- Custom dark matte theme with glass-effect cards (`GlassSurface`, `GlassSurfaceStrong`, `GlassSurfaceWeak`).
- Discord blurple accent (`#5865F2`), green play (`#10B981`), red stop (`#DC2626`).
- Animated buttons with `ScaleTransform` + `DropShadowEffect` glow on hover/press.
- Custom styled scrollbars, combo boxes, toggle switches, and text inputs.
- Ambient background with oscillating blurred ellipses (Discord blurple radial gradient).
