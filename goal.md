# Among Launcher - Development Goals

## Completed Features ✅

### Core Infrastructure
- [x] WPF desktop application with .NET 10
- [x] Custom dark matte theme with glass-effect cards
- [x] Discord blurple accent (#5865F2)
- [x] Animated buttons with glow effects
- [x] Custom styled scrollbars, combo boxes, toggle switches
- [x] Ambient background with oscillating blurred ellipses
- [x] Accessibility: ReduceMotion flag for animations

### Game Detection & Setup
- [x] Multi-storefront detection (Steam, Epic, Microsoft Store/Xbox)
- [x] Auto-detection fallback chain (Steam → Epic → Xbox)
- [x] Settings UI with storefront picker combo
- [x] Storefront picker modal for multiple installs
- [x] One-click setup: copies game, installs BepInEx 6 IL2CPP
- [x] Reset install option

### Discord Authentication
- [x] Discord OAuth2 (identify scope) via localhost:5000
- [x] Login flow with browser redirect
- [x] User data fetch (ID, username, global name, avatar)
- [x] Avatar display in sidebar
- [x] Logout with confirmation modal

### Game Management
- [x] Launch/stop game via GameProcessManager
- [x] PLAY/STOP toggle button
- [x] Storefront-aware launch args (-EpicPortal, --autopost, --server-url)
- [x] Browse files button
- [x] Game exit detection

### Mod Management
- [x] Installed mods list (BepInEx/plugins/*.dll)
- [x] Import local DLL
- [x] Remove mod with danger confirmation
- [x] To library functionality
- [x] GitHub preset mods (EHR, AUnlocker, Town of Us Mira, etc.)
- [x] Mod downloader with progress bars and retries
- [x] SHA-256 verification

### Mod Profiles
- [x] Save profile (name mod set as reusable preset)
- [x] Apply profile (diff, download missing, move extras to library)
- [x] Profile selector ComboBox

### Mod Library
- [x] Persistent storage in %LocalAppData%\AmongLauncher\Library
- [x] Add to library
- [x] Install from library
- [x] Remove from library
- [x] Pruning (removes entries with missing files)
- [x] Auto-move non-listed mods

### Mod Cleanup
- [x] Quarantine engine (moves mods to .disabled/)
- [x] Whitelist (AmongApi.dll, AUnlocker.dll, etc.)

### Lobby Backend Integration
- [x] REST client (LobbyBackendClient)
- [x] All endpoints: create, get, heartbeat, repost, kick, disband, mods
- [x] WebSocket client with reconnect/backoff
- [x] Host control panel (player list, repost, kick, disband)
- [x] Heartbeat service (30s interval)
- [x] Mod-set sync
- [x] In-game chat commands (/repost, /disband, /postlobby)
- [x] Max players support

### Deep-Link System
- [x] URI scheme registration (amongus-launcher, amonglauncher)
- [x] Single-instance routing via named pipe
- [x] Mod install deep-link parsing

### Among API Plugin
- [x] Named-pipe client with retry (5 attempts, 10s timeout)
- [x] Length-prefixed JSON wire format
- [x] Graceful disconnect handling
- [x] Reflection bridge (GameAssembly) with caching
- [x] Game state tracking (500ms polling)
- [x] Lobby joiner with main-thread dispatch
- [x] Chat command handler (/repost, /disband, /postlobby)
- [x] Auto-post functionality
- [x] Lobby leave functionality
- [x] File logging

### IPC & Infrastructure
- [x] Named-pipe server (AmongLauncher.IPC)
- [x] Named-pipe redirect server (AmongLauncher.Redirect)
- [x] IPC log viewer modal
- [x] Config persistence (config.json)
- [x] Config reload

---

## In Progress / Needs Work ⚠️

### Deep-Link Lobby Join
- [x] **Deep link lobby join pipeline** - ✅ FIXED
  - [x] Backend lobby fetch works
  - [x] Mod set sync works
  - [x] Game launch works
  - [x] **In-game join via IPC** - ✅ FIXED: MainThreadDispatcher context capture moved to Plugin.Load(), StartCoroutine parameter type mismatch fixed, SetRegion interface instantiation crash fixed
  - [x] **Region setting** - ✅ FIXED: Now checks existing built-in regions first before creating custom ones
  - [x] **StartCoroutine** - ✅ FIXED: Now searches for any 1-parameter StartCoroutine method and accepts IL2CPP enumerators

### Auto-Post Feature
- [x] **Auto-post sends mods** - ✅ FIXED: Added GetInstalledMods() to scan BepInEx/plugins/*.dll, compute SHA-256 hashes, and include full mod list
- [x] **Mod upload integrated** - ✅ FIXED: Mods are now uploaded with the lobby creation request

---

## Not Yet Implemented ❌

### Enhanced Lobby Features
- [ ] **Lobby browser/discovery** - Browse available lobbies
- [ ] **Lobby search** - Search by code, host, or mod type
- [ ] **Lobby history** - Remember recently joined lobbies (last 20)
- [ ] **Favorite lobbies** - Save frequently joined lobbies with notes
- [ ] **Lobby passwords** - Private lobbies with password protection
- [ ] **Lobby settings** - Configure map, max players, game speed before hosting
- [ ] **Lobby countdown** - Timer showing when lobby was created and estimated expiry
- [ ] **Player kick reason** - Show kick reason to kicked players
- [ ] **Lobby chat** - In-launcher chat between host and players before game starts

### Mod Management Enhancements
- [ ] **Mod updates** - Check for mod updates from GitHub
- [ ] **Mod versioning** - Track and display mod versions
- [ ] **Mod dependencies** - Auto-install required dependencies
- [ ] **Mod conflict detection** - Warn about incompatible mods
- [ ] **Mod profiles cloud sync** - Sync mod profiles across devices via backend
- [ ] **Mod presets sharing** - Share mod sets with other players via link
- [ ] **Auto-update all mods** - One-click update for all installed mods
- [ ] **Mod load order** - Configure DLL loading order for compatibility
- [ ] **Mod descriptions** - Show mod descriptions and changelogs
- [ ] **Mod screenshots** - Preview mod features with screenshots

### User Experience
- [ ] **Notifications** - Desktop notifications for lobby events
- [ ] **Tray icon** - Minimize to system tray
- [ ] **Auto-start** - Launch with Windows
- [ ] **Portable mode** - Run without installation
- [ ] **Dark/Light theme toggle** - Switch between dark and light themes
- [ ] **Custom accent colors** - User-selectable accent colors
- [ ] **Keyboard shortcuts** - Global hotkeys for common actions
- [ ] **In-app changelog** - Show changelog on update
- [ ] **Tutorial/onboarding** - First-run guide for new users
- [ ] **Tooltips** - Helpful tooltips for all UI elements
- [ ] **Window state persistence** - Remember window size and position
- [ ] **Recent servers** - Quick access to recently played servers

### Social Features
- [ ] **Friends list** - Add and manage friends
- [ ] **Party system** - Create parties and invite friends
- [ ] **In-app chat** - Chat with friends without Discord
- [ ] **User profiles** - Customizable profiles with avatar and bio
- [ ] **Online status** - Show online/in-game/idle status
- [ ] **Activity feed** - See what friends are playing
- [ ] **Invite system** - Send game invites via Discord or link

### Advanced Features
- [ ] **Multi-account support** - Switch between Discord accounts
- [ ] **Custom themes** - User-created theme packs
- [ ] **Plugin system** - Third-party extensions
- [ ] **Statistics** - Play time, mod usage stats, win/loss tracking
- [ ] **Replay system** - Record and replay games
- [ ] **Streaming integration** - OBS/Streamlabs overlay support
- [ ] **Custom game modes** - Support for modded game modes
- [ ] **Voice chat** - Built-in voice chat for lobbies
- [ ] **Screen sharing** - Share game screen with friends
- [ ] **Game recording** - Record gameplay clips

### Backend Integration
- [ ] **Lobby matchmaking** - Find players with similar mod sets
- [ ] **Mod repository** - Host mods on backend server
- [ ] **User profiles** - Extended user information
- [ ] **Friend system** - Add and invite friends
- [ ] **Leaderboards** - Global and mod-specific leaderboards
- [ ] **Achievements** - Unlockable achievements for milestones
- [ ] **Mod ratings/reviews** - Rate and review mods
- [ ] **Mod analytics** - Download counts, popularity stats
- [ ] **Server browser** - Browse multiple backend servers
- [ ] **Cross-server play** - Play across different backend instances

### Testing & Quality
- [ ] **Unit tests** - More comprehensive test coverage
- [ ] **Integration tests** - End-to-end testing
- [ ] **Error handling** - Better error messages and recovery
- [ ] **Performance optimization** - Faster mod loading and sync
- [ ] **Crash reporting** - Automatic crash report submission
- [ ] **Health checks** - Backend health monitoring
- [ ] **Load testing** - Stress test with many concurrent lobbies
- [ ] **Accessibility audit** - WCAG compliance review

### Security
- [ ] **Input validation** - Sanitize all user inputs
- [ ] **Rate limiting** - Prevent API abuse
- [ ] **Anti-cheat detection** - Detect and warn about cheats
- [ ] **Secure mod verification** - Cryptographic mod integrity checks
- [ ] **Privacy controls** - Control what information is shared

---

## Priority Order

### Phase 1: Fix Core Issues (Current Focus)
1. Fix deep-link lobby join pipeline
2. Fix auto-post to include actual mod list
3. Improve error handling and user feedback

### Phase 2: Enhanced Mod Management
1. Mod update checking
2. Mod dependency handling
3. Mod conflict detection

### Phase 3: User Experience
1. System tray integration
2. Notifications
3. Lobby browser
4. Dark/Light theme toggle
5. Keyboard shortcuts

### Phase 4: Social Features
1. Friends list
2. Party system
3. In-app chat
4. User profiles
5. Invite system

### Phase 5: Advanced Features
1. Multi-account support
2. Custom themes
3. Plugin system
4. Replay system
5. Voice chat

### Phase 6: Backend Integration
1. Matchmaking
2. Leaderboards
3. Achievements
4. Mod ratings/reviews
5. Server browser

### Phase 7: Quality & Security
1. Crash reporting
2. Accessibility audit
3. Security hardening
4. Performance optimization
5. Load testing

---

## Technical Debt

- [ ] **Code comments** - Many functions lack XML documentation
- [ ] **Error logging** - Inconsistent logging across components
- [ ] **Configuration validation** - Need better config validation
- [ ] **Memory management** - Some IDisposable objects may not be properly disposed
- [ ] **Thread safety** - Some shared state may have race conditions

---

## Success Criteria

### Minimum Viable Product (MVP)
- [x] Users can install and launch modded Among Us
- [x] Users can manage mods (import, remove, profiles)
- [x] Users can host lobbies with auto-post
- [ ] Users can join lobbies via deep links (currently broken)

### Version 1.0 Release
- [ ] All MVP features working reliably
- [ ] Deep-link join working end-to-end
- [ ] Comprehensive error handling
- [ ] Basic documentation

### Version 2.0 Release
- [ ] Lobby browser and discovery
- [ ] Mod update system
- [ ] Advanced user features
- [ ] Plugin system

---

## Notes

- The deep-link join issue is the most critical current problem
- The in-game join mechanism uses reflection which may break with game updates
- The backend API is stable and well-tested
- The UI/UX is polished but could use more accessibility features
