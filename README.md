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
- **Custom Modals** — Dark-themed overlay system for confirmations and preset mod library
- **AmongAPI IPC** — Named pipe communication between the launcher and the in-game mod
- **Dark Matte Theme** — Premium UI with glow effects, animated buttons, and status pill

## Project Structure

```
Among Launcher.sln              Solution file
global.json                     .NET SDK version config
README.md                       This file
FEATURES.md                     Complete feature documentation
launcher-spec.md                Technical specification
goal.md                         Development goals and roadmap
api.md                          IPC protocol specification
agent.md                        Agent configuration

Among Launcher/                 WPF desktop application
├── Among Launcher.csproj       Project file (.NET 10 WPF)
├── App.xaml                    Application entry point XAML
├── App.xaml.cs                 Application startup, single-instance, deep-link handling
├── MainWindow.xaml             Main window with sidebar navigation
├── MainWindow.xaml.cs          Window logic, IPC server setup, deep-link routing
├── GlobalUsings.cs             Global using directives
│
├── Auth/
│   └── DiscordAuthService.cs   Discord OAuth2 login (localhost:5000 callback)
│
├── Config/
│   └── LauncherConfig.cs       Settings persistence (config.json)
│
├── Game/
│   └── GameProcessManager.cs   Launch/stop Among Us process
│
├── GameDetection/
│   ├── Storefront.cs           Enum: Steam, Epic, MicrosoftStore
│   ├── GameSearchResult.cs     Detection result record
│   ├── GameFinder.cs           Epic & Xbox game detection
│   ├── SteamFinder.cs          Steam library detection
│   └── AmongUsLocator.cs       Auto-detection orchestrator
│
├── Installer/
│   ├── BepInExInstaller.cs     BepInEx 6 IL2CPP installation
│   └── GameCopier.cs           Copy vanilla game to modded folder
│
├── Ipc/
│   └── PipeServer.cs           Named pipe server (AmongLauncher.IPC)
│
├── Models/
│   ├── BackendModels.cs        API request/response records
│   ├── DiscordUserProfile.cs   Discord user data
│   ├── LibraryEntry.cs         Mod library entry
│   ├── LobbyInfo.cs            Lobby data model
│   ├── ModDownloadItem.cs      Download queue item
│   ├── ModInfo.cs              Installed mod info
│   ├── ModProfile.cs           Saved mod profile
│   ├── ModSetEntry.cs          Mod set entry (file + URL)
│   └── PresetMod.cs            GitHub preset mod definition
│
├── Services/
│   ├── DeepLinkHandler.cs      URI scheme parsing (amonglauncher://, amongus-launcher://)
│   ├── LauncherLog.cs          File logging
│   ├── ModDownloader.cs        Download with retries & SHA-256 verification
│   ├── Sha256Helper.cs         Hash computation
│   ├── SingleInstance.cs       Global mutex + redirect pipe
│   │
│   └── Lobby/
│       ├── LobbyBackendClient.cs      REST API client (create, get, heartbeat, kick, disband)
│       ├── LobbyWebSocketClient.cs    WebSocket for live updates (kick, rejoin)
│       ├── LobbyHeartbeatService.cs   30s keepalive timer
│       ├── LobbyJoinService.cs        Join pipeline orchestrator
│       ├── LobbyCommandService.cs     WebSocket command dispatcher
│       ├── LobbyBotClient.cs          Discord bot WebSocket client
│       ├── LobbyTypeDetector.cs       Detect modded/vanilla from plugins
│       ├── ModSetSync.cs              Diff & sync mods with lobby
│       ├── ModProfileManager.cs       Save/load mod profiles
│       ├── ModCleanupEngine.cs        Quarantine non-required mods
│       └── LibraryManager.cs          Mod library operations
│
└── Views/
    ├── AmbientBackground.xaml(.cs)        Animated blurred ellipses background
    ├── WelcomeView.xaml(.cs)              Discord OAuth login screen
    ├── MainView.xaml(.cs)                 Game status, mod list, play/stop
    ├── SettingsView.xaml(.cs)             Server URL, bot endpoint, toggles
    ├── LibraryView.xaml(.cs)              Mod library browser
    ├── HostControlPanelView.xaml(.cs)     Live player list, repost/kick/disband
    ├── ModalOverlay.xaml(.cs)             Generic modal container
    ├── ConfirmationModal.xaml(.cs)        Confirm/cancel dialog
    ├── DownloadModsModal.xaml(.cs)        Sequential download progress
    ├── JoinDebugModal.xaml(.cs)           Debug join status display
    ├── LogViewerModal.xaml(.cs)           IPC log viewer
    ├── PresetModLibraryModal.xaml(.cs)    GitHub preset mods
    ├── MsStoreAccessModal.xaml(.cs)       MS Store permission guide
    ├── StorefrontPickerModal.xaml(.cs)    Multiple install picker
    └── LibraryPickerModal.xaml(.cs)       Library mod picker

Among API/                    BepInEx IL2CPP plugin (runs inside Among Us)
├── Among API.csproj          Project file (.NET 6, BepInEx 6)
├── Plugin.cs                 Entry point, IPC setup, lobby lifecycle
├── GlobalUsings.cs           Global using directives
│
└── Services/
    ├── PipeClient.cs              Named pipe client with retry
    ├── GameAssembly.cs            Reflection bridge (IL2CPP interop)
    ├── GameStateTracker.cs        500ms polling for lobby/player changes
    ├── LobbyJoiner.cs             In-game lobby join via reflection
    ├── ChatCommandHandler.cs      /repost, /disband, /postlobby commands
    ├── MainThreadDispatcher.cs    Unity main-thread dispatch
    └── FileLogger.cs              BepInEx/AmongApi.log writer

AmongLauncher.Tests/          Integration tests
├── AmongLauncher.Tests.csproj Project file (xUnit)
└── BackendIntegrationTests.cs Live backend API tests

BepInEx/                      BepInEx runtime (Steam build)
└── dotnet/                    .NET runtime files

BepInEx-MS-Epic/              BepInEx runtime (Microsoft Store/Epic build)
└── dotnet/                    .NET runtime files

docs/                         Documentation
├── adaptation-guide.md        Codebase adaptation guide
└── superpowers/
    ├── specs/                 Design specifications
    ├── plans/                 Implementation plans
    └── research/              Research documents
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
