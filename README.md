<div align="center">

# Among Launcher

**Play modded Among Us with friends — one launcher for setup, mods, and lobbies.**

[![Platform](https://img.shields.io/badge/platform-Windows-0078D6?logo=windows&logoColor=white)](#requirements)
[![Tauri](https://img.shields.io/badge/Tauri-2-24C8DB?logo=tauri&logoColor=white)](https://v2.tauri.app/)
[![React](https://img.shields.io/badge/React-19-61DAFB?logo=react&logoColor=black)](https://react.dev/)
[![Rust](https://img.shields.io/badge/Rust-stable-000000?logo=rust&logoColor=white)](https://www.rust-lang.org/)
[![License](https://img.shields.io/badge/license-Private-red)](#license)
[![Downloads](https://img.shields.io/badge/Downloads-Releases-2ea44f?logo=github&logoColor=white)](https://github.com/FirethCrafts/Among-Launcher/releases)

</div>

Among Launcher is a Windows desktop launcher that makes modded Among Us approachable: it finds your game, installs the mod loader, and keeps your mods in one place. It's built for players who want to host or join modded lobbies without manually editing game folders.

The project has three parts:

- **Among Launcher** — a Tauri (React + Rust) desktop app for setup, game launch, mods, and lobby hosting.
- **AmongApi** — a BepInEx 6 IL2CPP plugin that runs inside Among Us and talks to the launcher over a local named pipe.
- **Backend + dashboard** — a self-hosted FastAPI service and React dashboard that power lobby listings, join links, and heartbeats.

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Building from Source](#building-from-source)
- [Usage](#usage)
- [Project Structure](#project-structure)
- [Tech Stack](#tech-stack)
- [Troubleshooting](#troubleshooting)
- [Links](#links)
- [License](#license)

## Features

### 🎮 Game & Setup

- **Multi-storefront detection** — finds Among Us automatically across **Steam**, **Epic Games**, and the **Microsoft Store**.
- **One-click setup** — copies your game into a separate modded folder (`%LocalAppData%\AmongLauncher\ModdedAmongUs`), installs BepInEx 6 IL2CPP, and drops in `AmongApi.dll`.
- **Manual fallback** — if detection misses, browse to `Among Us.exe` directly or pick your storefront in Settings.
- **Launch & stop** — start the modded game and close it from the launcher, with a running-state indicator.
- **Install status & progress** — live badges for BepInEx/AmongApi and a progress bar during setup.

### 🏠 Lobbies & Multiplayer

- **Discord sign-in** — log in with Discord via a deep-link OAuth callback; your avatar and name show in the app.
- **Host Control Panel** — when you host in-game, the launcher mirrors your lobby to the backend and shows a live player list (names, levels, pings, host tag).
- **Host actions** — post your lobby, kick players, and disband, with an Online/Offline server-heartbeat indicator.
- **Deep-link join** — opening `amonglauncher://join?code=CODE` forwards to the running launcher and asks the in-game mod to join that lobby.
- **Single-instance routing** — deep links sent to an already-running launcher are handed to that instance instead of opening a second copy.
- **In-game join screen** — enter a lobby code and join directly while the game is connected.

### 🧩 Mods

- **Installed mod list** — see every `.dll` in your modded install with its version and size.
- **Import local DLLs** — add your own mods from disk.
- **Remove mods** — clean up a mod from the modded install with one click.
- **Browse files** — open a mod's folder in Explorer.
- **Mod library** — save installed mods into a reusable library and reinstall them later with one click.

### 🔄 Updates & Experience

- **AmongApi update check** — compares the installed `AmongApi.dll` version against the latest `mod/v*` release and installs it in-app.
- **Launcher self-update** — checks for a newer `launcher/v*` release and shows an in-app prompt with a download link.
- **Runtime version display** — Settings shows the version of the running launcher.
- **Atomic config persistence** — settings are written via temp-file rename with a backup (`%LocalAppData%\AmongLauncher\config.json`).
- **Single window, no background process** — closing the launcher exits it completely (no system tray).
- **Dark glass UI** — a single-window React interface with a custom titlebar and a dark, translucent theme.

### 🔌 In-Game IPC

- **Named-pipe bridge** — the launcher runs a pipe server and the mod runs a pipe client on `\\.\pipe\AmongLauncher.IPC`.
- **Length-prefixed JSON** — a small framed protocol carrying game state (`lobby_created`, `player_joined`, `join_lobby`, and more).
- **Game state tracking** — the mod polls lobby/player changes and reports them to the launcher.
- **File logging** — the mod writes `BepInEx/AmongApi.log` next to your modded install for troubleshooting.

## Requirements

- **Windows 10 or 11** (64-bit)
- **Among Us** — Steam, Epic Games, or Microsoft Store
- **A Discord account** — required for sign-in and lobby features
- **An internet connection** — for Discord login, lobby hosting, and update checks

## Installation

1. Open the [Releases page](https://github.com/FirethCrafts/Among-Launcher/releases).
2. Download the latest launcher installer (`Among.Launcher_<version>_x64-setup.exe`).
3. Run the installer and launch **Among Launcher**.
4. Sign in with **Discord** on the welcome screen.
5. On first run, let the launcher **detect your game**, then click **Install** to create the modded copy with BepInEx and AmongApi.
6. Once setup completes, press **Launch** and play.

> **SmartScreen note:** the installer is not code-signed, so Windows may show an "unknown publisher" warning. Choose **More info → Run anyway** to continue.

## Building from Source

### Prerequisites

- **Node.js 20+** and npm
- **Rust (stable)** with the MSVC toolchain — see [rustup.rs](https://rustup.rs/)
- **Tauri prerequisites** — WebView2 runtime and MSVC build tools — see the [Tauri prerequisites guide](https://v2.tauri.app/start/prerequisites/)
- **.NET SDK 6.0** — to build the in-game mod
- **.NET SDK 10** — to build/test the legacy WPF launcher

### Launcher (Tauri)

```bash
git clone https://github.com/FirethCrafts/Among-Launcher.git
cd Among-Launcher/launcher-tauri
npm ci
npm run tauri:dev      # run in development
npm run tauri:build    # production build + installer
```

Other useful scripts (run from `launcher-tauri/`):

```bash
npm run dev            # Vite frontend only
npm run typecheck      # TypeScript type check
cd src-tauri && cargo test   # Rust tests
```

To reproduce a published installer (version bump, NSIS bundle, GitHub release), run the PowerShell release script from the repo root:

```powershell
./release.ps1
```

### In-Game Mod

```bash
dotnet build "Among API/Among API.csproj" -c Release
```

The plugin targets **.NET 6** and outputs `AmongApi.dll`, which belongs in your modded install's `BepInEx/Plugins/` folder.

### Legacy Tests

```bash
dotnet test AmongLauncher.Tests/AmongLauncher.Tests.csproj
```

## Usage

- **Setup** — Home shows *Setup Needed* until BepInEx and AmongApi are present; use **Set Up Game** to detect, browse for, and install the modded copy.
- **Play** — press **Launch** to start Among Us. The **In Game** screen becomes available once the mod connects.
- **Host** — create a lobby in-game; the **Host Control Panel** appears so you can post it to the server, watch players join, kick, or disband.
- **Join** — enter a code on the **In Game** screen, or open an `amonglauncher://join?code=CODE` link while the launcher is running.
- **Mods** — import local DLLs or install from your saved library on **Home**; manage saved mods on the **Library** page.
- **Settings** — sign in/out, view detected and modded game paths, choose or auto-detect your storefront, and check the launcher version.

## Project Structure

```
Among Launcher/
├── launcher-tauri/            Tauri desktop launcher (current)
│   ├── src/                   React + Vite frontend (pages, components, state)
│   ├── src-tauri/             Rust core (commands, IPC server, lobby, updater)
│   └── versions.json          Tracks the AmongApi version
├── Among API/                 BepInEx 6 IL2CPP plugin (.NET 6) — in-game mod
│   ├── Plugin.cs              Entry point, IPC handlers, lobby lifecycle
│   └── Services/              Pipe client, game state tracker, joiner, logging
├── Among Launcher/            Legacy WPF launcher (superseded by launcher-tauri)
├── AmongLauncher.Tests/       xUnit tests for legacy services
├── BepInEx/                   Bundled BepInEx runtime (Steam)
├── BepInEx-MS-Epic/           Bundled BepInEx runtime (Epic / Microsoft Store)
├── api.md                     IPC + backend protocol specification
└── release.ps1                Builds and publishes the launcher release
```

## Tech Stack

| Layer | Technology |
|-------|------------|
| Launcher UI | React 19 + Vite 6 + Tailwind CSS 4 |
| Launcher core | Rust + Tauri v2 |
| In-game mod | BepInEx 6 IL2CPP plugin, .NET 6 (C#) |
| IPC | Windows named pipes, length-prefixed JSON |
| Auth | Discord OAuth2 (deep-link callback) |
| Backend | FastAPI (separate repository) |
| Dashboard | React |

## Troubleshooting

- **Among Us isn't detected** — open **Settings → Storefront**, pick your platform (or **Auto-detect**), or use **Browse** on the setup screen to point at `Among Us.exe`.
- **BepInEx install fails** — close Among Us before installing, make sure the game folder and `%LocalAppData%` are writable, then retry. The launcher asks you to close the game if a file is locked.
- **No mod update detected** — check you're online and that `BepInEx/Plugins/AmongApi.dll` exists in the modded install; reinstall it from setup if it's missing.
- **SmartScreen warns about the installer** — the installer is unsigned; choose **More info → Run anyway**.
- **Host controls show Offline or posting fails** — sign in with Discord, confirm the **In Game** badge shows connected, and make sure the backend is reachable.

## Links

- **Releases:** https://github.com/FirethCrafts/Among-Launcher/releases
- **Backend / dashboard:** https://among-us.mel-homes.com
- **Backend repository:** https://github.com/FirethCrafts/Among-Backend
- **IPC & backend protocol:** [api.md](api.md)

## License

Private — All rights reserved.
