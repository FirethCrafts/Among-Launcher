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
├── Services/           Mod sync, file management, mod loader
├── Config/             Plugin configuration
├── Contracts/          Interfaces for mod system
├── Models/             Mod manifest and entry models
└── Plugin.cs           BepInEx plugin entry point
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

## Tech Stack

| Component | Technology |
|-----------|------------|
| UI | WPF (.NET 10) |
| Theme | Custom dark matte with glow effects |
| IPC | Windows Named Pipes |
| Auth | Discord OAuth2 |
| Mod Source | GitHub Releases API |
| Plugin | BepInEx 6 IL2CPP (.NET 6) |

## License

Private — All rights reserved.
