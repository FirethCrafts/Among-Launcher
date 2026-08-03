# Among Us Join API — Research Spike (Task 1)

- Date: 2026-08-03
- Task: `Task 1: Research Spike — Among Us Join API & Assembly Reference Strategy`
- Consumes: installed modded Among Us at `%LOCALAPPDATA%\AmongLauncher\ModdedAmongUs`
- Produces: verified game API surface for the "join lobby on command" feature + the assembly reference strategy that Tasks 13–16 must use.
- Feature context: a BepInEx IL2CPP plugin (`Among API`) receives `join_lobby { code, region, regionIp }` from the launcher over IPC, registers/selects a custom region on `ServerManager`, decodes the lobby code via `GameCode`, and calls the game's join-by-code coroutine.

> **IMPORTANT — legacy naming correction:** the plan/spec referred to `AmongUsClient.JoinOnlineGame`. **This method does not exist in the installed build.** The current public equivalent is `AmongUsClient.CoJoinOnlineGameFromCode(int gameId, bool fromEnterCode = false)`. All downstream tasks (13–16) must use the names in this doc, not `JoinOnlineGame`.

---

## 1. Game install, build identity, and tooling

| Item | Value | Evidence |
|---|---|---|
| Install path | `C:\Users\meowfire\AppData\Local\AmongLauncher\ModdedAmongUs` | config.json `ModdedInstallPath` |
| Interop folder | `BepInEx\interop\` — 120 DLLs, 51.6 MB total | `Get-ChildItem` |
| `Assembly-CSharp.dll` | Present, 7.7 MB | confirmed |
| Unity version | `2022.3.44f1 (c3ae09b9f03c)` | `Among Us.exe` PE version resource; `Player.log` |
| IL2CPP metadata version | 31 | `BepInEx\LogOutput.log` ("Using actual IL2CPP Metadata version 31") |
| Doorstop / BepInEx | Doorstop `target_assembly=BepInEx\core\BepInEx.Unity.IL2CPP.dll`, BepInEx 6.0.0-be.735 | `doorstop_config.ini`, `LogOutput.log` |
| Decompiler | `ilspycmd` 10.1.1 (dotnet tool, installed for this task) | `dotnet tool install --global ilspycmd` |
| Signatures extracted | `ilspycmd -t "<Type>"` on `BepInEx\interop\Assembly-CSharp.dll` / `UnityEngine.CoreModule.dll` | — |

**Game release version string: NOT directly extractable from static files.** All game string literals live in `global-metadata.dat` as LZ4-compressed string-literal data (IL2CPP metadata v31); raw string scans of the metadata, `GameAssembly.dll`, and `Player.log` found no `vYYYY.M.D` string. The build evidence (Unity `2022.3.44f1`, IL2CPP metadata v31, presence of `DetectiveRoleOptionsV10`/`ViperRoleOptionsV10`/`HttpMatchmakerManager`/`StaticHttpRegionInfo`) is consistent with a **2025-era Among Us release (v2025.x, inferred)**. The launcher's game-version detection (GameFinder) does not currently record a version either.

> The interop wrappers are generated from the game's real metadata at first launch, so **every member signature below is verified from the installed binary**, not from public docs. Items explicitly labelled *inferred* come from public Among Us modding knowledge.

---

## 2. Join API — `AmongUsClient` (global namespace, `Assembly-CSharp.dll`)

Declared as `public class AmongUsClient : InnerNetClient`. Members below are the **exact decompiled signatures**.

### 2.1 Singleton + scene fields

```csharp
public static AmongUsClient Instance { get; set; }          // static field-backed property
public string OnlineScene { get; set; }                     // e.g. "OnlineGame"
public string MainMenuScene { get; set; }
public GameData GameDataPrefab { get; set; }
public PlayerControl PlayerPrefab { get; set; }
```

### 2.2 Join / create coroutine methods (all return a coroutine enumerator)

```csharp
// Host: create an online lobby. Returns enumerator; must be StartCoroutine'd.
public IEnumerator CoCreateOnlineGame();

// Join by lobby code (THE join-by-code entry point).
// gameId = GameCode.GameNameToInt(code). Resolves the lobby through the currently
// selected region's matchmaker, then connects.
public IEnumerator CoJoinOnlineGameFromCode(int gameId, bool fromEnterCode = false);

// Quick-play public join straight to a host server.
public IEnumerator CoJoinOnlinePublicGame(int gameId, string ipAddress, ushort port,
    MainMenuTarget targetMenu = MainMenuTarget.OnlineMenu);

// Join from an existing GameListing (public lobby browser path).
public IEnumerator CoJoinOnlineGameFromListing(GameListing game, string matchmakerToken);

// Matchmaker helpers.
public IEnumerator CoFindGameInfoFromCode(int gameId,
    Il2CppSystem.Action<HttpMatchmakerManager.FindGameByCodeResponse, string> callback);
public IEnumerator CoFindGameInfoFromCodeAndJoin(int gameId);
public IEnumerator CoFindGame();

// Private internals (not for direct use).
private IEnumerator CoConnectToGameServer(MatchMakerModes mode, string ipAddress,
    ushort port, string matchmakerToken);
private IEnumerator CoJoinOnlineGameDirect(int gameId, string ipAddress,
    ushort port, string matchmakerToken);
```

The `IEnumerator` return type resolves to `Il2CppSystem.Collections.IEnumerator` (the file's usings include `using Il2CppSystem.Collections;`). Coroutines are executed by passing the enumerator to `MonoBehaviour.StartCoroutine(IEnumerator)` (see §7).

### 2.3 Lifecycle hooks (useful Harmony patch targets / state signals)

Protected virtuals on `AmongUsClient` (override of `InnerNetClient`):

```csharp
protected override void OnGameCreated(string gameIdString);   // host: lobby created
protected override void OnWaitForHost(string gameIdString);
protected override void OnGameJoined(string gameIdString);    // client: join succeeded
protected override void OnPlayerJoined(ClientData data);
protected override void OnPlayerLeft(ClientData data, DisconnectReasons reason);
protected override void OnBecomeHost();
protected override void OnGameEnd(EndGameResult endGameResult);
protected override void OnDisconnected();
public void ExitGame(DisconnectReasons reason);
```

`public override void Update()` exists — a patchable per-frame hook.

---

## 3. Game-state / connection state — `InnerNet.InnerNetClient` (namespace `InnerNet`)

```csharp
public class InnerNetClient : MonoBehaviour
{
    public enum GameStates { NotJoined, Joined, Started, Ended }   // NOT "WaitingHost"
    public GameStates GameState { get; set; }     // field-backed property
    public bool InOnlineScene { get; set; }       // field-backed property
    public int GameId { get; set; }               // int lobby id; code = GameCode.IntToGameName(GameId)
    public int HostId { get; set; }
    public NetworkModes NetworkMode { get; set; }
    public static int CurrentClient;
    public static int HostInherit;
}
```

`InnerNetClient.GameStates` is the client connection state. There is a **separate** enum `InnerNet.GameStates : byte { NotStarted, Started, Ended, Destroyed }` — that one describes a lobby/game's match state (used by `GameListing`); it is **not** the "in lobby" signal.

**Lobby detection (for Task 14 `GameStateTracker`)** — recommended signal, all verified:
- `LobbyBehaviour.Instance != null` ⇒ the `OnlineGame`/lobby scene is active (lobby UI object exists). This is the cleanest "currently in a lobby" test.
- `AmongUsClient.Instance != null && AmongUsClient.Instance.GameState == InnerNetClient.GameStates.Joined` (NotStarted on the *other* enum) ⇒ connected to a lobby that has not started.
- `AmongUsClient.Instance != null && AmongUsClient.Instance.InOnlineScene` ⇒ in an online scene.
- Player count: `GameData.Instance.PlayerCount` (int).
- Lobby code string: `GameCode.IntToGameName(AmongUsClient.Instance.GameId)`.
- Lobby closed: `GameState` returns to `NotJoined`, or `LobbyBehaviour.Instance` becomes null, or `OnDisconnected`/`OnGameEnd` fires.

---

## 4. Code ↔ int — `InnerNet.GameCode` (namespace `InnerNet`, static)

```csharp
public static class GameCode : Il2CppSystem.Object
{
    public static int    GameNameToInt(string gameId);   // 6-char code -> int (V1/V2 auto-selected)
    public static string IntToGameName(int gameId);      // int -> 6-char code
    public static int    CreateGameId(int sn, int gn);   // sn * MaxGameNumber + gn

    // runtime-computed tables/constants (initialized in native code):
    public static int V2Flag;
    public static int MaxGameNumber;
    public static int GameCodeV2MinVersion;
    public static string V2;                             // V2 alphabet
    public static Il2CppStructArray<int> V2Map;
    // private: IntToGameNameV2(int), GameNameToIntV2(string)
}
```

- **Use `GameCode.GameNameToInt(code)` directly.** The public methods dispatch between the legacy (V1) and V2 alphabets internally based on the game version (`GameCodeV2MinVersion`, `V2Map`) — the `<>c` cache shows both V1/V2 lambda delegates wired into the public statics. Do **not** re-implement the alphabet.
- The installed build supports V2 codes (V2Flag/V2Map fields exist, join flow is `CoJoinOnlineGameFromCode`).

---

## 5. Regions & custom region injection — `ServerManager`

`public class ServerManager : DestroyableSingleton<ServerManager>` — the singleton is **inherited** (see §5.3), *not* declared on `ServerManager` itself.

### 5.1 Members (verified)

```csharp
public IRegionInfo CurrentRegion { get; }                  // selected region
public ServerInfo  CurrentUdpServer { get; }               // selected server
public bool        IsHttp { get; }                         // true when using HTTP matchmaker
public string      TargetServer { get; }
public Il2CppReferenceArray<IRegionInfo> AvailableRegions { get; }
public Il2CppReferenceArray<ServerInfo>  AvailableServers { get; }
public string      UdpNetAddress { get; }
public ushort      UdpNetPort { get; }
public bool        UdpUseDtls { get; }
public UpdateState state { get; }                          // Connecting/Failed/Success/PartialSuccess

public void AddOrUpdateRegion(IRegionInfo newRegion);      // register or replace a region
public void SetRegion(IRegionInfo region);                 // SELECT the region (see note below)
public void ReselectServer();
public void SaveServers();
public void LoadServers();
public IEnumerator ReselectRegionFromDefaults();
public IEnumerator WaitForServers();
public bool TrackServerFailure(string networkAddress);

public static Il2CppReferenceArray<IRegionInfo> DefaultRegions;
public static bool  useDtls;
public static float PingTimeoutSeconds;
```

> **IMPORTANT — legacy naming correction:** older public modding docs describe `ServerManager.ChooseRegion(int idx)`. **`ChooseRegion` does not exist in this build.** Region selection is `SetRegion(IRegionInfo)`. Use the members above.

### 5.2 Region types (verified)

```csharp
// abstract region contract
public abstract class IRegionInfo : Il2CppObjectBase
{
    public virtual string Name { get; }
    public virtual string PingServer { get; }
    public virtual Il2CppReferenceArray<ServerInfo> Servers { get; }
    public virtual StringNames TranslateName { get; }
    public virtual string TargetServer { get; }
    public virtual IRegionInfo Duplicate();
    public virtual bool Validate();
}

// UDP/DNS region — custom region with hostname + port
public class DnsRegionInfo : Il2CppSystem.Object
{
    public string Fqdn;        public string DefaultIp;
    public ushort Port;        public bool UseDtls;
    public DnsRegionInfo(string fqdn, string name, StringNames translateName,
                         string defaultIp, ushort port, bool useDtls = true);
    public void PopulateServers();
}

// HTTP matchmaker region — the shape used by THIS install's custom regions
public class StaticHttpRegionInfo : Il2CppSystem.Object
{
    public StaticHttpRegionInfo(string name, StringNames translateName, string pingServer,
                                Il2CppReferenceArray<ServerInfo> servers, string targetServer = null);
}

// legacy static region
public class StaticRegionInfo : Il2CppSystem.Object
{
    public StaticRegionInfo(string name, StringNames translateName, string pingServer,
                            Il2CppReferenceArray<ServerInfo> servers, string targetServer = null);
}

// one server entry (fields read/write via interop field access)
public class ServerInfo : Il2CppSystem.Object
{
    public string Name;  public string Ip;  public ushort Port;
    public bool   UseDtls; public int Players; public int ConnectionFailures;
    public string HttpUrl { get; }
    public ServerInfo(string name, string ip, ushort port, bool useDtls);
}
```

### 5.3 Singleton access

`ServerManager` has no own `Instance` — it inherits from `DestroyableSingleton<T>`:

```csharp
public class DestroyableSingleton<T> : MonoBehaviour where T : MonoBehaviour
{
    public static T   Instance;        // DestroyableSingleton<ServerManager>.Instance
    public static bool InstanceExists;
}
```

Through reflection this is reached via `ServerManagerType.BaseType.GetProperty("Instance")` (the base is the closed generic `DestroyableSingleton<ServerManager>`).

### 5.4 Real-world confirmation (from the live install)

`%USERPROFILE%\AppData\LocalLow\Innersloth\Among Us\regionInfo.json` is `ServerManager.SaveServers()` output. It confirms the exact serialized shape and that custom regions are already injected into this install using `StaticHttpRegionInfo` with HTTP matchmaker URLs:

```json
{ "CurrentRegionIdx": 3, "Regions": [
  { "$type":"StaticHttpRegionInfo, Assembly-CSharp",
    "Name":"Clazau", "PingServer":"amongus.clazau.org",
    "Servers":[ { "Name":"Http-1", "Ip":"https://amongus.clazau.org",
                  "Port":443, "UseDtls":false, "Players":0, "ConnectionFailures":0 } ],
    "TargetServer":null, "TranslateName":1003 },
  { "$type":"StaticHttpRegionInfo, Assembly-CSharp",
    "Name":"Modded EU (MEU)", "PingServer":"https://au-eu.duikbo.at",
    "Servers":[ { "Name":"Http-1", "Ip":"https://au-eu.duikbo.at", "Port":443,
                  "UseDtls":false, "Players":0, "ConnectionFailures":0 } ],
    "TargetServer":null, "TranslateName":1003 } ] }
```

- `TranslateName = 1003` == `StringNames.NoTranslation` (verified against the decompiled enum). Custom regions set the display name via `Name` and `TranslateName = StringNames.NoTranslation`.
- Current custom-region practice on this build: **`StaticHttpRegionInfo` with `Ip = "https://<host>"`, `Port = 443`** (HTTP matchmaker). `DnsRegionInfo` (UDP) still exists but the install's active regions are HTTP.

### 5.5 Custom region join sequence (documented order)

For Task 15 `LobbyJoiner`, in order:

1. `ServerManager` (via `DestroyableSingleton<ServerManager>.Instance`).
2. Build a region object — `StaticHttpRegionInfo(name, StringNames.NoTranslation, pingServer, servers)` where `servers = [ ServerInfo(name, ip, port, useDtls) ]` (HTTP: `ip = "https://host"`, `port = 443`, `useDtls = false`). (Legacy/UDP fallback: `DnsRegionInfo(fqdn, name, NoTranslation, defaultIp, port, useDtls)`.)
3. `ServerManager.AddOrUpdateRegion(region)`.
4. `ServerManager.SetRegion(region)`.
5. `int gameId = GameCode.GameNameToInt(code)`.
6. `AmongUsClient.Instance.CoJoinOnlineGameFromCode(gameId, fromEnterCode: false)` → `StartCoroutine(enumerator)` (§7).
7. Success/failure: `GameState` reaching `Joined` while `InOnlineScene`/`LobbyBehaviour` present ⇒ success; `JoinFailureReasons`/disconnect ⇒ error.

> Joining by code goes through the currently selected region's matchmaker (`CoJoinOnlineGameFromCode` → `CoFindGameInfoFromCodeAndJoin` → `CoJoinOnlineGameDirect(host.ip, host.port, token)`), so the custom region must be selected *before* calling it.

---

## 6. Other types used by later tasks

| Type | Verified members |
|---|---|
| `GameData : MonoBehaviour` | `public static GameData Instance`; `public int PlayerCount` |
| `LobbyBehaviour : InnerNetObject` | `public static LobbyBehaviour Instance` |
| `PlayerControl` | present (type exists); detailed members out of scope for this spike |
| `ShipStatus` + map subclasses (`SkeldShipStatus`, `MiraShipStatus`, `PolusShipStatus`, `AirshipStatus`, `FungleShipStatus`) | present |
| `GameStartManager` | `GameRoomNameCode` field (private, shows code text); `MinPlayers`, `LastPlayerCount` |
| `InnerNet.MatchMakerModes : enum` | `None, Client, HostAndClient` |
| `UpdateState : enum` | `Connecting, Failed, Success, PartialSuccess` |
| `InnerNet.JoinFailureReasons : enum` | exists (values *inferred*) |
| `UnityEngine.MonoBehaviour` | `public Coroutine StartCoroutine(IEnumerator)` (in `UnityEngine.CoreModule.dll`); param type is `Il2CppSystem.Collections.IEnumerator` |

---

## 7. Coroutine execution & threading

- `CoCreateOnlineGame` / `CoJoinOnlineGameFromCode` return `Il2CppSystem.Collections.IEnumerator`; they must be started on `UnityEngine.MonoBehaviour.StartCoroutine(IEnumerator)` (verified overload `StartCoroutine_Public_Coroutine_IEnumerator_0`). `AmongUsClient.Instance` is itself a `MonoBehaviour`, so `AmongUsClient.Instance.StartCoroutine(enumerator)` is the game's own pattern.
- **Thread affinity:** Unity APIs and coroutine starts must run on the Unity main thread. `PipeClient` handles arrive on the plugin's async loop thread, so Task 15 must marshal the join call to the main thread (e.g., enqueue the command and consume it in a plugin-owned `MonoBehaviour.Update`, or equivalent). This is a plugin-side implementation detail (out of this doc's API scope), but mandatory for the coroutine join to work.

---

## 8. Assembly reference strategy — DECISION

### Context
- `Among API\Among API.csproj` currently targets `net6.0` and references **only NuGet packages** (`BepInEx.Unity.IL2CPP` `6.0.0-be.*`, `BepInEx.PluginInfoProps`). Zero game-assembly references.
- CI (`\.github\workflows\build.yml`) restores/builds on `ubuntu-latest` with `dotnet-version: 6.0.x` and **no game files**; the release artifact is `bin/Release/net6.0/AmongApi.dll` — that DLL is the shipped plugin.
- The game's interop set is 120 DLLs / 51.6 MB and is **regenerated on every game update** (Cpp2IL at first launch). Committing it for CI would bloat the repo and drift on each update.

### Options considered

**Option 1 — compile-time `<Reference>` to the interop assemblies (HintPath, conditional `Exists`).**
- Gives compile-time type safety locally.
- Breaks CI: with the files absent, game-typed code cannot compile, so the shipped CI artifact would be a stub unless every game-typed file is wrapped in `#if` guards / a separate project. Either way the real join code is excluded from the artifact that CI ships. Requires maintaining the full 51.6 MB interop set. **Rejected.**

**Option 2 — keep the csproj game-reference-free; access the game via `System.Reflection` at runtime.** ✔ **CHOSEN**
- The plugin compiles everywhere (CI and local) against `net6.0` + BepInEx packages only — exactly the current CI setup; no csproj or CI changes needed.
- At runtime the game always has the interop assemblies loaded (BepInEx\interop), so reflection resolves real game types with zero shipping overhead.
- Robust to game updates: members are resolved lazily and failures degrade to a logged error instead of a load-time type mismatch.
- Cost: no compile-time checking — mitigated by this doc pinning exact names, and by a small typed-access helper (`GameAssembly`) in the plugin.

### The strategy Tasks 13–16 MUST use

> **Use Option 2: no game-assembly references in `Among API.csproj`. All game-typed code calls are `System.Reflection` against the interop assemblies at runtime, resolved from the modded install's `BepInEx\interop` folder.**

Implementation notes for the plugin (`Among API`):

- Resolve types lazily with a helper, e.g. `GameAssembly.GetType("AmongUsClient")`:
  1. Search `AppDomain.CurrentDomain.GetAssemblies()` for `Assembly-CSharp` first (already loaded by BepInEx).
  2. Fall back to `Assembly.LoadFrom(Path.Combine(interopDir, "Assembly-CSharp.dll"))`, where `interopDir` is derived from the game directory (e.g. `Path.Combine(Environment.CurrentDirectory, "BepInEx", "interop")`).
  3. Cache `Type` + `MemberInfo` objects; wrap resolution failures in `try/catch` and log, never crash `Load()`.
- Runtime type mapping for `Il2CppReferenceArray<T>` / `Il2CppStructArray<T>`: these are real .NET arrays of the wrapper type at runtime; build them with `Array.CreateInstance(type, n)`.
- Construct Il2Cpp objects with `Activator.CreateInstance(type, args)` (the public ctor is a normal .NET ctor that routes through `il2cpp_object_new`).
- Static property access (singletons) via `GetProperty(name).GetValue(null)`; instance property/method access via normal reflection on the returned wrapper objects.
- Exact member names to use (all verified in §2–§5):
  - `AmongUsClient.Instance` (static), `CoJoinOnlineGameFromCode(int, bool)`, `CoCreateOnlineGame()`, `GameState`, `InOnlineScene`, `GameId`
  - `InnerNetClient.GameStates` enum, `InnerNet.GameStates` enum
  - `GameCode.GameNameToInt(string)` / `GameCode.IntToGameName(int)` (static)
  - `ServerManager` singleton via base type `DestroyableSingleton<ServerManager>`.Instance; `AddOrUpdateRegion(IRegionInfo)`, `SetRegion(IRegionInfo)`, `AvailableRegions`, `CurrentRegion`
  - `StaticHttpRegionInfo(string, StringNames, string, ServerInfo[], string)` (primary), `DnsRegionInfo(string, string, StringNames, string, ushort, bool)` (UDP fallback), `ServerInfo(string, string, ushort, bool)`, `StringNames.NoTranslation` (== 1003)
  - `UnityEngine.MonoBehaviour.StartCoroutine(IEnumerator)` on `AmongUsClient.Instance`
  - Lobby state: `LobbyBehaviour.Instance`, `GameData.Instance.PlayerCount`

---

## 9. Verification status summary

| Claim | Status |
|---|---|
| `AmongUsClient` join/create coroutine signatures (§2) | ✅ Verified from `Assembly-CSharp.dll` (ilspycmd) |
| No `JoinOnlineGame` method in this build; it is `CoJoinOnlineGameFromCode` | ✅ Verified (full member search) |
| `InnerNetClient.GameState`, `InOnlineScene`, `GameId`, `HostId`, both `GameStates` enums | ✅ Verified |
| `GameCode.GameNameToInt` / `IntToGameName` / `CreateGameId` + V2 fields | ✅ Verified |
| `ServerManager` members incl. `SetRegion`/`AddOrUpdateRegion`, no `ChooseRegion` | ✅ Verified |
| `DestroyableSingleton<T>.Instance` / `InstanceExists` | ✅ Verified |
| `IRegionInfo`, `DnsRegionInfo`, `StaticRegionInfo`, `StaticHttpRegionInfo`, `ServerInfo` signatures | ✅ Verified |
| `StaticHttpRegionInfo` + `StringNames.NoTranslation=1003` custom-region shape | ✅ Verified from live `regionInfo.json` + enum |
| `GameData.Instance.PlayerCount`, `LobbyBehaviour.Instance` | ✅ Verified |
| `MonoBehaviour.StartCoroutine(IEnumerator)` overload | ✅ Verified from `UnityEngine.CoreModule.dll` |
| `UpdateState`, `MatchMakerModes` enum values | ✅ Verified |
| Exact game release version string (e.g. `v2025.x.x`) | ⚠️ Inferred (LZ4-compressed metadata; see §1) |
| V1/V2 code alphabet internals (not needed — use `GameNameToInt`) | ⚠️ Inferred (public modding docs) |
| `MainMenuTarget` member values; `JoinFailureReasons` member values | ⚠️ Inferred |
| Main-thread marshalling pattern for coroutine start | ⚠️ Inferred (standard Unity/BepInEx practice) |

---

## 10. What Tasks 13–16 should take from this doc

- **Task 13 (PipeClient dispatch):** no game API needed; unchanged by this doc.
- **Task 14 (GameStateTracker):** poll per §3 — `LobbyBehaviour.Instance`, `GameState`, `GameData.Instance.PlayerCount`, `GameCode.IntToGameName(GameId)`; emit `lobby_created`/`lobby_closed`/`player_joined`/`player_left` on transitions. All reads via the `GameAssembly` reflection helper (Option 2).
- **Task 15 (LobbyJoiner):** implement §5.5 sequence (region → `SetRegion` → `GameCode.GameNameToInt` → `CoJoinOnlineGameFromCode` → `StartCoroutine`) on the main thread; report `join_lobby_result`.
- **Task 16 (ChatCommands):** chat-send hook lives in `PlayerControl`/`PlayerControl.RpcSendChat`-style surface (type confirmed present; exact members to be resolved in Task 16 using the same reflection helper).
- All four tasks: **do not add interop `<Reference>`s to the csproj**; use the reflection helper.
