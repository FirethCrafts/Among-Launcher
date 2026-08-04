# AmongLauncher

A modern Among Us mod launcher built with WPF (.NET 10), featuring Discord OAuth login, automatic BepInEx installation, mod management, and real-time IPC communication with the AmongAPI plugin.

## Features

- **Steam Detection** — Automatically finds Among Us in your Steam library
- **One-Click Setup** — Copies your game and installs BepInEx 6 IL2CPP
- **Discord OAuth** — Log in with your Discord account, profile avatar displayed in sidebar
- **Game Management** — Launch/stop Among Us with running status indicator
- **Mod Management** — Import local DLLs or install preset mods from GitHub repositories
- **Mod Profiles** — Save and load named mod sets as reusable presets
- **Deep-Link Lobby Join** — `amonglauncher://join?code=ABCDEF` links sync the lobby's mod set, launch the game, and join in-game automatically
- **Single-Instance Routing** — Deep links sent to an already-running instance are forwarded to it instead of launching a second copy
- **Host Control Panel** — Live player list with repost, kick, and disband controls while hosting
- **Self-Hosted Backend** — Optional lobby backend integration: create/fetch/repost/kick/disband and heartbeat, plus WebSocket-driven kick and rejoin
- **Custom Modals** — Dark-themed overlay system for confirmations and preset mod library
- **AmongAPI IPC** — Named pipe communication between the launcher and the in-game mod
- **Dark Matte Theme** — Premium UI with glow effects, animated buttons, and status pill

## Project Structure

```
AmongLauncher/          WPF application
├── Auth/               Discord OAuth2 service
├── Config/             Launcher settings persistence
├── Game/               Game process management (launch/stop)
├── Installer/          BepInEx, game copy, plugin installer
├── Ipc/                Named pipe server/client for AmongAPI
├── Models/             Data models (ModInfo, ModSetEntry, ModProfile, LobbyInfo, DiscordUserProfile)
├── Services/           Deep links, single-instance routing, mod downloads
│   └── Lobby/          Lobby backend client, WebSocket, join pipeline, heartbeat, mod profiles
├── Steam/              Steam library detection
└── Views/              UI views (MainView, SettingsView, WelcomeView, HostControlPanelView, modals)

Among API/              BepInEx IL2CPP plugin (runs inside Among Us)
├── Services/           Pipe client, game-state tracker, lobby joiner, chat commands
└── Plugin.cs           BepInEx plugin entry point

Among Backend/          Self-hosted lobby backend (ASP.NET Core)
├── Services/           Lobby store, WebSocket hub, Discord embed notifier, heartbeat expiry
├── Models/             Lobby / mod-set models
└── Program.cs          REST + WebSocket endpoints
```

## Getting Started

### Prerequisites

- Windows 10/11
- .NET 10 SDK
- Among Us (Steam)
- A Discord application with OAuth2 configured (for login)

### Build

```bash
dotnet build Among\ Launcher.sln
```

### Run

```bash
dotnet run --project "Among Launcher/Among Launcher.csproj"
```

## Discord OAuth Setup

1. Create a Discord application at https://discord.com/developers/applications
2. Add a redirect URI: `http://localhost:5000/callback/`
3. Copy your Client ID and Client Secret
4. Update `ClientSecret` in `Among Launcher/Auth/DiscordAuthService.cs`

## AmongAPI IPC Protocol

The launcher communicates with the in-game AmongAPI plugin via named pipes. See [api.md](api.md) for the full protocol specification.

**Quick overview:**
- Pipe name: `AmongLauncher.IPC`
- Transport: Length-prefixed JSON over Windows Named Pipes
- Bidirectional — either side can send messages at any time

## Among Backend

The self-hosted lobby backend lives in `Among Backend/` (ASP.NET Core, .NET 10). It stores lobby state in memory, exposes the REST + WebSocket contract the launcher consumes, and can post/update/remove a live Discord invite embed via a webhook.

```bash
dotnet run --project "Among Backend/Among Backend.csproj"
```

By default it listens on **`http://localhost:5013`** (see `Among Backend/Properties/launchSettings.json`).

- Point the launcher's **Server URL** setting at the backend root — e.g. `http://localhost:5013` when running locally (the REST routes live at `/lobby`, `/ws`, etc.; do **not** append `/api` — the backend has no such prefix). For a real deployment use `https://yourserver.com`.
- Point the **Bot WS Endpoint** at `ws://localhost:5013/ws` locally, or `wss://yourserver.com/ws` for a real deployment.
- Set `Discord:WebhookUrl` in `Among Backend/appsettings.json` to enable the invite embed; leave it empty to run without Discord.
- `Lobby:HeartbeatGraceSeconds` (default 90) controls how long a host can stop heartbeating before the backend expires its lobby.
- To run the backend on a different port: `dotnet run --project "Among Backend/Among Backend.csproj" --urls http://localhost:8000` — then use that port in the launcher's Server URL instead.

Full REST + WebSocket contract: see [api.md](api.md).

## Tech Stack

| Component | Technology |
|-----------|------------|
| UI | WPF (.NET 10) |
| Theme | Custom dark matte with glow effects |
| IPC | Windows Named Pipes |
| Auth | Discord OAuth2 |
| Mod Source | GitHub Releases API |
| Plugin | BepInEx 6 IL2CPP (.NET 6) |
| Backend | ASP.NET Core (.NET 10) |

## License

Private — All rights reserved.
