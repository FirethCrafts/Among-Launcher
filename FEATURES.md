# Features

This document lists the features of the **Among Launcher** (WPF desktop app) and the **Among API** (BepInEx IL2CPP mod DLL that runs inside Among Us), based on the current source.

## Among Launcher (WPF desktop app)

### Game detection & setup
- Multi-storefront detection: Steam (via `libraryfolders.vdf` + fallback paths), Epic Games (manifest `.item` files, `GameUserSettings.ini`, fallback paths), and Microsoft Store / Xbox (drive scan + `WindowsApps`).
- Auto-detection that falls through storefronts in order (Steam → Epic → Xbox).
- Settings UI to pick a storefront (Steam/Epic/MS Store/Auto), auto-scan, and a picker modal when multiple installs are found.
- Manual folder browse with storefront auto-matching.
- One-click setup: copies the game to `%LocalAppData%\AmongLauncher\ModdedAmongUs`, installs BepInEx 6 IL2CPP (storefront-specific build from bundled files or a GitHub release asset), writes `steam_appid.txt` for Steam, installs `AmongApi.dll`, all with progress reporting.
- Reset-install option (deletes the modded copy).

### Discord auth & user
- Discord OAuth2 (`identify` scope) via a local HTTP callback listener.
- Fetches user ID, username, global name, and avatar; persists avatar + username to config and shows the avatar in the sidebar.
- Logout flow with confirmation.

### Game management
- Launch/stop the modded Among Us executable; running-status indicator, PLAY/STOP toggle, and a status pill.
- Storefront-aware launch args (`-EpicPortal` for Epic).
- Browse open the modded install folder.
- Detect game exit and update the UI.

### Mod management
- List installed mods (DLLs in `BepInEx/plugins`) with file sizes.
- Import a local `.dll` (with overwrite-conflict handling) into plugins.
- Mod library: copy mods into a persistent Library folder, install them back into plugins, remove from library.
- Install preset mods from GitHub repositories (latest release asset selection with preferred-name matching, `.dll` fallback) via a preset library modal.
- Remove mods with a danger confirmation modal.
- Mod profiles: save a named mod set, apply a profile (diff against installed, download missing, and move non-profile mods to the library).
- Mod cleanup: quarantines mods that are not in the lobby's required set to `BepInEx/plugins/.disabled` (never hard-deleted).

### Lobby integration / deep links
- Deep-link install: `amongus-launcher://install?mods=<url1>,<url2>` downloads and installs mods then launches the game.
- Deep-link join: `amonglauncher://join?code=XXXXXX` sends a GET request to the backend at `/api/v1/lobbies/<code>`, syncs the mod set, launches the game, waits for readiness, and tells the in-game mod to join.
- Auto-registers both custom URI protocols in the registry.
- Single-instance routing: deep links are forwarded to an already-running instance instead of opening a second copy.

### Lobby backend integration
- REST client communicating with the Python FastAPI backend at `/api/v1/lobbies/` with optional bearer token auth.
- Endpoints: create/refresh lobby (`POST`), fetch lobby (`GET`), heartbeat, repost, kick (`player_id`), disband.
- Lobby data sent to the backend uses the Python backend's format: `host` (player name), `mod_type`, `mods[]` with `name`/`version`/`file_hash`.
- WebSocket client with reconnect/backoff that receives live `kick` and `rejoin` commands from the backend.
- Host control panel: live player list, repost, kick, and disband controls; disband gated by a confirmation modal.
- Heartbeat service that keeps the host's lobby alive every 30s (no body sent).
- Mod-set sync: diff the lobby's mod set against local plugins and install missing DLLs before joining.
- In-game chat commands `/repost` and `/disband` surface from the mod into the launcher's host flow.

### Discord bot integration
- Sends lobby creation events to a Discord bot via WebSocket for forum thread creation.
- Detects lobby type (modded/vanilla) and selects the configured role ID per type.

### IPC & infrastructure
- Named-pipe server (`AmongLauncher.IPC`) for bidirectional JSON messaging with the in-game mod; status of the connection shown in the UI.
- Named-pipe redirect server (`AmongLauncher.Redirect`) for single-instance deep-link forwarding.
- IPC log viewer modal.
- Persistence via `config.json` (storefront, server URLs, Discord token/avatar/username, profiles, library, bot WS endpoint, role IDs).
- Custom dark-matte theme with glow effects, animated buttons/modals, and a `ReduceMotion` accessibility toggle.
- Welcome screen with entrance animations.

## Among API (BepInEx IL2CPP mod DLL, runs inside Among Us)

### IPC client
- Named-pipe client that connects to the launcher with retry/backoff (5 attempts), sends length-prefixed JSON frames, registers request handlers, and reports ACK/disconnect.
- Graceful behavior when the launcher isn't running — mods still load normally.

### Reflection bridge
- Lazy, cached reflection helper resolving game types/members at runtime with zero compile-time game-assembly references. Reads properties/fields, calls static/instance methods, constructs instances and generic types, resolves enums, and matches methods by arg count/assignability. All failures degrade to `null` + log, never throw.

### Game state tracking
- 500ms polling loop that detects lobby enter/leave (host-gated), tracks player-count changes (join/leave events), and reads the lobby code via `GameCode.IntToGameName`. Emits `lobby_created`, `lobby_closed`, `player_joined`, `player_left` events to the launcher.

### Direct in-game lobby join
- Background queue pump that executes the join sequence — builds a `StaticHttpRegionInfo` + `ServerInfo` array, calls `AddOrUpdateRegion`/`SetRegion` on `ServerManager`, decodes the code with `GameCode.GameNameToInt`, and starts the `CoJoinOnlineGameFromCode` coroutine. Returns a `JoinResult` to the launcher (success or error message, with timeout).

### In-game chat commands
- 500ms poll of the free-chat field; detects `/repost` and `/disband`, clears the input so commands aren't sent as chat, and fires callbacks to the launcher over the pipe.

### Lobby leave
- Uses `AmongUsClient.Instance.ExitGame(DisconnectReasons.ExitGame)` via reflection (used by `/disband`).

### Logging
- Writes timestamped INFO/WARN/ERROR entries to `BepInEx/AmongApi.log`, plus BepInEx console logging.
