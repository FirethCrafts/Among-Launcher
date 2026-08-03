# Lobby Join & Reconnect Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a host create an Among Us lobby on a custom region, auto-post an `amonglauncher://join?code=...` invite via a self-hosted backend/Discord bot, and let a joiner click the link so the launcher sets up, installs the lobby's mods, launches the game, and commands the in-game mod to join — plus a host dashboard with kick/repost/disband and automatic reconnect after host mod changes.

**Architecture:** The launcher is the orchestrator: it owns deep-link handling, backend REST + WebSocket, mod-set install, and game lifecycle. The in-game AmongAPI plugin stays thin — it gains only (a) an inbound IPC dispatch path (currently missing), (b) lobby lifecycle/player-tracking hooks, and (c) a direct-join handler. A self-hosted backend (user-owned) stores lobby state and pushes `kick`/`rejoin` over WebSocket; a Discord bot (user-owned) posts and live-edits the invite embed. The mod's in-game join work is gated on a research spike against the installed game version.

**Tech Stack:** .NET 10 WPF (`Among Launcher`), .NET 6 BepInEx IL2CPP (`Among API`), Windows Named Pipes, System.Net.WebSockets, Harmony, JSON.

## Global Constraints

- Pipe name: `AmongLauncher.IPC`. Frame = `[4-byte little-endian length][UTF-8 JSON]`, max 1 MB, `Flush()` after write.
- Envelope: `{ "type": string, "id": "<8-char hex>", "timestamp": long, "payload": object? }`. Responses echo the requester's `id`.
- The launcher is the single-instance owner of `AmongLauncher.IPC` (server side). The plugin connects as client.
- Mods dir: `%LOCALAPPDATA%\AmongLauncher\ModdedAmongUs\BepInEx\plugins`.
- Config: `%LOCALAPPDATA%\AmongLauncher\config.json` via `LauncherConfig`.
- Deep-link schemes: `amonglauncher://join?code=ALSKDJ` (new) AND `amongus-launcher://install?mods=...` (existing — must keep working).
- File-lock rule: never overwrite/delete a plugin `.dll` while the game process is running. Kill with `KillGame()`, then `process.WaitForExit()` (bounded 15s), then retry file ops up to 5× with backoff (250ms/500ms/1s/2s/4s).
- Boot crash guard: after launch, watch `Process.HasExited` immediately; `game_ready` timeout is 90s and aborts early on process exit.
- Both projects must build: `dotnet build "Among Launcher.sln"`. No new NuGet packages unless listed in a task.
- No game-assembly references in the plugin until Task 1 (research spike) resolves the reference strategy. CI (`build.yml`) builds the plugin with zero game refs — the join feature ships in the same DLL using reflection-free IL2CPP interop only if the spike proves it viable; otherwise join code is a runtime-optional assembly loaded only when the game assemblies are present.
- `api.md` must be kept in sync — update it in the same commit as any IPC change.
- No hardcoded secrets. The Discord OAuth `ClientId`/`ClientSecret` already live in `Auth\DiscordAuthService.cs` — do not touch or log them.

---

### Task 1: Research Spike — Among Us Join API & Assembly Reference Strategy

**Files:**
- Create: `docs/superpowers/research/among-us-join-api.md`

**Interfaces:**
- Consumes: installed Among Us (Steam) at a path found via `GameDetection\GameFinder`.
- Produces: a written research doc `docs/superpowers/research/among-us-join-api.md` with exact signatures for: `AmongUsClient.JoinOnlineGame`, `ServerManager` region registration/selection, custom region injection (ip/port fields), code→int decoding (`GameCode`), the lobby-creation game-state transition to hook, and the chosen IL2CPP assembly reference strategy for the `Among API` csproj + CI.

- [ ] **Step 1: Locate the game and its IL2CPP interop assemblies**

Run the launcher's detection logic, or set the config manually, to find the Among Us install. Identify the modded install at `%LOCALAPPDATA%\AmongLauncher\ModdedAmongUs` and confirm `BepInEx\interop\Assembly-CSharp.dll` and `BepInEx\interop\Il2Cpp*.dll` exist after one game launch. If interop assemblies are absent, note that the plugin currently builds without them (CI builds AmongApi.dll from source) and that game-typed code must live behind a boundary that can be compiled independently.

- [ ] **Step 2: Document the join + region + code APIs**

Use ILSpy/dnSpy (or grep the interop DLLs) on the installed game's `BepInEx\interop\Assembly-CSharp.dll` to record, verbatim in the research doc:
- `AmongUsClient` — the exact `JoinOnlineGame` overloads and their signatures, and the `OnlineScene`/`GameState` members used to detect a host lobby and to join.
- `ServerManager` — members for `AvailableRegions`, `ChooseRegion`, and how a custom region (`DnsRegionInfo` or equivalent, its `Ip`/`Port`/`Name` fields) is registered and selected.
- `GameCode` (or equivalent) — the static method converting a 6-char lobby code to an int, and int → name.
- The game-state property/enum value that becomes `WaitingHost`/`Lobby` when the host creates a lobby (the hook point).
- The installed game version string.

- [ ] **Step 3: Decide and document the assembly reference strategy**

Choose ONE of these and write the decision + rationale in the research doc:
1. Reference the game's interop assemblies from the local modded install via `<Reference>` HintPaths in the csproj, keeping CI buildable by making those references conditional on the files existing.
2. Keep the csproj game-reference-free; write join code as `System.Reflection` calls against the interop assembly at runtime (load `Assembly-CSharp.dll` from the modded install), so CI never needs game files.

State explicitly which one the mod-side tasks (Tasks 10-13) must use, and give the exact member names those tasks reference.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/research/among-us-join-api.md
git commit -m "docs: among us join api research spike"
```

---

### Task 2: Config + Models for Lobby & Profiles

**Files:**
- Modify: `Among Launcher\Config\LauncherConfig.cs`
- Create: `Among Launcher\Models\ModSetEntry.cs`, `Among Launcher\Models\ModProfile.cs`, `Among Launcher\Models\LobbyInfo.cs`, `Among Launcher\Models\BackendModels.cs`

**Interfaces:**
- Consumes: existing `LauncherConfig` (fields `ServerUrl`, `ModdedInstallPath`, `AvatarUrl`, `UserName`).
- Produces:
  - `LauncherConfig.BackendWssUrl` (string), `LauncherConfig.DiscordAccessToken` (string?), `LauncherConfig.DiscordTokenExpiry` (long), `LauncherConfig.Profiles` (List<ModProfile>).
  - `class ModSetEntry { string FileName; string DownloadUrl; string? Sha256; string? Version; }`
  - `class ModProfile { string Name; List<ModSetEntry> Mods; }`
  - `class LobbyInfo { string Code; string Region; string RegionIp; int RegionPort; List<ModSetEntry> ModSet; string? HostUserId; int PlayerCount; }`
  - `class BackendModels` containing records: `CreateLobbyRequest`, `LobbyResponse`, `LobbyPlayer`, `HeartbeatRequest` — serialized to the JSON shapes in the spec's endpoint table.

- [ ] **Step 1: Add config fields**

Add to `LauncherConfig.cs` (after `UserName`):
```csharp
public string BackendWssUrl { get; set; } = "wss://yourserver.com/ws";
public string DiscordAccessToken { get; set; } = string.Empty;
public long DiscordTokenExpiry { get; set; }
public List<ModProfile> Profiles { get; set; } = new();
```
Add `using System.Collections.Generic;` if not implicitly available.

- [ ] **Step 2: Create the model files**

Create `Models\ModSetEntry.cs`, `Models\ModProfile.cs`, `Models\LobbyInfo.cs` as described in Interfaces. Create `Models\BackendModels.cs`:
```csharp
namespace AmongLauncher.Models;

public record CreateLobbyRequest(string Code, string Region, string RegionIp, int RegionPort, List<ModSetEntry> ModSet, string? HostUserId);
public record LobbyResponse(string Code, string Region, string RegionIp, int RegionPort, List<ModSetEntry> ModSet, string? HostUserId, int PlayerCount);
public record LobbyPlayer(string DiscordUserId, string? PlayerName, bool IsHost);
public record HeartbeatRequest(string Code, string HostUserId);
```

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Config/LauncherConfig.cs" "Among Launcher/Models/ModSetEntry.cs" "Among Launcher/Models/ModProfile.cs" "Among Launcher/Models/LobbyInfo.cs" "Among Launcher/Models/BackendModels.cs"
git commit -m "feat: add lobby and mod profile models plus config fields"
```

---

### Task 3: DeepLinkHandler — Join URI + Shared Parse Logic

**Files:**
- Modify: `Among Launcher\Services\DeepLinkHandler.cs`
- Test: `Among Launcher\Services\DeepLinkHandler.cs` (self-contained; verified by a temporary console harness or inline asserts in a `Main`-less unit — see step 3)

**Interfaces:**
- Consumes: existing `DeepLinkHandler.Scheme` (`"amongus-launcher"`), `FindDeepLinkArgument`, `Parse`, `RegisterProtocol`.
- Produces:
  - `const string JoinScheme = "amonglauncher"`
  - `record JoinRequest(string Code);`
  - `static string? FindDeepLinkArgument()` — now also matches `amonglauncher://`.
  - `static JoinRequest? TryParseJoin(string deepLink)` — returns `{ Code }` for `amonglauncher://join?code=ALSKDJ` (case-insensitive), else `null`.
  - `static void RegisterProtocol()` — registers BOTH schemes to the same exe.

- [ ] **Step 1: Extend scheme detection and add join parsing**

In `DeepLinkHandler.cs`, change `FindDeepLinkArgument` to also accept the new scheme:
```csharp
public static string? FindDeepLinkArgument()
{
    var args = Environment.GetCommandLineArgs();
    return args.FirstOrDefault(a =>
        a.StartsWith($"{Scheme}://", StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith($"{JoinScheme}://", StringComparison.OrdinalIgnoreCase));
}
```
Add the `JoinRequest` record and `TryParseJoin`:
```csharp
public record JoinRequest(string Code);

public static JoinRequest? TryParseJoin(string deepLink)
{
    if (!Uri.TryCreate(deepLink, UriKind.Absolute, out var uri))
        return null;
    if (!string.Equals(uri.Scheme, JoinScheme, StringComparison.OrdinalIgnoreCase))
        return null;
    if (!string.Equals(uri.Host, "join", StringComparison.OrdinalIgnoreCase))
        return null;
    var code = ExtractParam(uri.Query.TrimStart('?'), "code");
    if (string.IsNullOrWhiteSpace(code))
        return null;
    code = Uri.UnescapeDataString(code).Trim().ToUpperInvariant();
    if (code.Length < 4 || code.Length > 8)
        return null;
    return new JoinRequest(code);
}
```

- [ ] **Step 2: Register both schemes**

Change `RegisterProtocol` to loop over both schemes:
```csharp
public static void RegisterProtocol()
{
    try
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return;
        foreach (var scheme in new[] { Scheme, JoinScheme })
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}");
            key.SetValue("", $"URL:{scheme} Protocol");
            key.SetValue("URL Protocol", "");
            using var shell = key.CreateSubKey(@"shell\open\command");
            shell.SetValue("", $"\"{exePath}\" \"%1\"");
        }
    }
    catch
    {
        // Best effort - protocol registration failure shouldn't break startup
    }
}
```

- [ ] **Step 3: Verify parse logic**

Run a scratch check (add temporarily in `App.xaml.cs` `OnStartup` under `#if DEBUG`, or a throwaway `dotnet run` console; remove before commit):
```
TryParseJoin("amonglauncher://join?code=ALSKDJ")      -> JoinRequest { Code = "ALSKDJ" }
TryParseJoin("amonglauncher://join?code=alSKDJ")      -> JoinRequest { Code = "ALSKDJ" }
TryParseJoin("amonglauncher://install?mods=x")        -> null
TryParseJoin("amonglauncher://join")                  -> null
TryParseJoin("amongus-launcher://install?mods=x")     -> null (existing install path unaffected)
```
Expected: as shown.

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Services/DeepLinkHandler.cs"
git commit -m "feat: deep link join URI parsing and dual scheme registration"
```

---

### Task 4: Single-Instance Deep-Link Routing

**Files:**
- Create: `Among Launcher\Services\SingleInstance.cs`
- Modify: `Among Launcher\App.xaml.cs`, `Among Launcher\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `DeepLinkHandler.FindDeepLinkArgument()`.
- Produces:
  - `class SingleInstance` with `static bool TryBecomePrimary(out SingleInstance? primary)` (mutex `Global\AmongLauncher.SingleInstance`), `static void StartRedirectServer(Action<string> onDeepLink)` (NamedPipe server `AmongLauncher.Redirect`), `static void ForwardDeepLink(string arg)` (secondary instance connects and sends the URI).
  - `MainWindow` exposes `public void HandleDeepLink(string deepLink)` (replaces private `HandleDeepLink()`).

- [ ] **Step 1: Write SingleInstance**

Create `Services\SingleInstance.cs`:
```csharp
using System.IO.Pipes;
using System.Text;

namespace AmongLauncher.Services;

public static class SingleInstance
{
    private const string MutexName = @"Global\AmongLauncher.SingleInstance";
    private const string PipeName = "AmongLauncher.Redirect";

    public static bool TryBecomePrimary(out Mutex? mutex)
    {
        mutex = new Mutex(true, MutexName, out var createdNew);
        if (createdNew) return true;
        mutex.Dispose();
        mutex = null;
        return false;
    }

    public static void StartRedirectServer(Action<string> onDeepLink)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var link = await reader.ReadLineAsync();
                    if (!string.IsNullOrEmpty(link))
                        onDeepLink(link);
                }
                catch { await Task.Delay(1000); }
            }
        });
    }

    public static void ForwardDeepLink(string deepLink)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine(deepLink);
        }
        catch { }
    }
}
```

- [ ] **Step 2: Wire primary/secondary logic in App.xaml.cs**

Replace `App.xaml.cs` empty class with:
```csharp
using System.Windows;
using AmongLauncher.Services;

namespace AmongLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var deepLink = DeepLinkHandler.FindDeepLinkArgument();

        if (!SingleInstance.TryBecomePrimary(out _))
        {
            if (deepLink != null)
                SingleInstance.ForwardDeepLink(deepLink);
            Shutdown();
            return;
        }
    }
}
```

- [ ] **Step 3: Route URI to MainWindow and pass it in**

In `MainWindow.xaml.cs`:
- Change the ctor to accept `string? deepLink = null`.
- After `ShowView(...)`, if `deepLink != null` call `HandleDeepLink(deepLink)` in `Loaded`.
- Rename private `HandleDeepLink()` (line ~176) to `public void HandleDeepLink(string? deepLink)` that calls `FindDeepLinkArgument()` when null, then branches: `TryParseJoin(link)` → join flow (Task 7 wiring), else existing `Parse` → `ShowDownloadModsModal`.
- Register the redirect handler in `App` after becoming primary (pass `MainWindow` creation into a static hook, or simpler: `App.OnStartup` keeps `deepLink` and passes to the window; `SingleInstance.StartRedirectServer` is started in `OnStartup` with a callback that raises a static event `DeepLinkReceived`, which `MainWindow` subscribes to and dispatches to `HandleDeepLink`).

Keep it minimal: add `public static event Action<string>? DeepLinkReceived;` to `App`, start the redirect server in `OnStartup`, and in `MainWindow` ctor subscribe `App.DeepLinkReceived += link => Dispatcher.Invoke(() => HandleDeepLink(link));`.

- [ ] **Step 4: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 5: Verify single-instance**

Run the exe once (primary), then run it a second time with arg `amonglauncher://join?code=ALSKDJ` (e.g. via `start "" "path\AmongLauncher.exe" "amonglauncher://join?code=ALSKDJ"`). Expected: second process exits immediately; primary logs a deep-link received line. Then verify one non-join install link still works: `amongus-launcher://install?mods=https://example.com/mods/a.dll`.

- [ ] **Step 6: Commit**

```bash
git add "Among Launcher/Services/SingleInstance.cs" "Among Launcher/App.xaml.cs" "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: single instance deep link routing via redirect pipe"
```

---

### Task 5: Backend REST Client

**Files:**
- Create: `Among Launcher\Services\Lobby\LobbyBackendClient.cs`

**Interfaces:**
- Consumes: `LauncherConfig.ServerUrl`, `LauncherConfig.DiscordAccessToken`, `Models\BackendModels`, `Models\LobbyInfo`.
- Produces:
  - `class LobbyBackendClient(HttpClient http, LauncherConfig config)` with:
    - `Task<LobbyInfo?> GetLobbyAsync(string code, CancellationToken ct)`
    - `Task<bool> CreateLobbyAsync(CreateLobbyRequest req, CancellationToken ct)`
    - `Task<bool> RepostAsync(string code, CancellationToken ct)`
    - `Task<bool> KickAsync(string code, string targetUserId, CancellationToken ct)`
    - `Task<bool> DisbandAsync(string code, CancellationToken ct)`
    - `Task<bool> HeartbeatAsync(string code, string hostUserId, CancellationToken ct)`
  - Every request sets `Authorization: Bearer <DiscordAccessToken>` (skipped if empty). Base address = `config.ServerUrl`.

- [ ] **Step 1: Write the client**

Create `Services\Lobby\LobbyBackendClient.cs`:
```csharp
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class LobbyBackendClient
{
    private readonly HttpClient _http;
    private readonly LauncherConfig _config;

    public LobbyBackendClient(HttpClient http, LauncherConfig config)
    {
        _http = http;
        _config = config;
        _http.BaseAddress = new Uri(config.ServerUrl.TrimEnd('/') + "/");
    }

    private void ApplyAuth(HttpRequestMessage msg)
    {
        if (!string.IsNullOrEmpty(_config.DiscordAccessToken))
            msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.DiscordAccessToken);
    }

    public async Task<LobbyInfo?> GetLobbyAsync(string code, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"lobby/{code}");
        ApplyAuth(msg);
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadFromJsonAsync<LobbyResponse>(cancellationToken: ct);
        if (body == null) return null;
        return new LobbyInfo
        {
            Code = body.Code,
            Region = body.Region,
            RegionIp = body.RegionIp,
            RegionPort = body.RegionPort,
            ModSet = body.ModSet ?? new List<ModSetEntry>(),
            HostUserId = body.HostUserId,
            PlayerCount = body.PlayerCount
        };
    }

    public async Task<bool> CreateLobbyAsync(CreateLobbyRequest req, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, "lobby") { Content = JsonContent.Create(req) };
        ApplyAuth(msg);
        using var resp = await _http.SendAsync(msg, ct);
        return resp.IsSuccessStatusCode;
    }

    public Task<bool> RepostAsync(string code, CancellationToken ct) =>
        PostNoContent($"lobby/{code}/repost", ct);

    public Task<bool> KickAsync(string code, string targetUserId, CancellationToken ct) =>
        PostNoContent($"lobby/{code}/kick", ct, new { targetUserId });

    public Task<bool> DisbandAsync(string code, CancellationToken ct) =>
        DeleteNoContent($"lobby/{code}", ct);

    public Task<bool> HeartbeatAsync(string code, string hostUserId, CancellationToken ct) =>
        PostNoContent($"lobby/{code}/heartbeat", ct, new { hostUserId });

    private async Task<bool> PostNoContent(string path, CancellationToken ct, object? body = null)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, path);
            ApplyAuth(msg);
            if (body != null) msg.Content = JsonContent.Create(body);
            using var resp = await _http.SendAsync(msg, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<bool> DeleteNoContent(string path, CancellationToken ct)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Delete, path);
            ApplyAuth(msg);
            using var resp = await _http.SendAsync(msg, ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Services/Lobby/LobbyBackendClient.cs"
git commit -m "feat: lobby backend REST client"
```

---

### Task 6: WebSocket Command Receiver

**Files:**
- Create: `Among Launcher\Services\Lobby\LobbyWebSocketClient.cs`

**Interfaces:**
- Consumes: `LauncherConfig.BackendWssUrl`, `LauncherConfig.DiscordAccessToken`.
- Produces:
  - `class LobbyWebSocketClient` with:
    - `Task ConnectAsync(string lobbyCode, CancellationToken ct)`
    - `event EventHandler<string>? Kicked;` (payload: reason)
    - `event EventHandler<RejoinCommand>? Rejoin;`
    - `void Disconnect()`
  - `record RejoinCommand(string LobbyCode, List<ModSetEntry> ModSet, string Region, string RegionIp, int RegionPort);`

- [ ] **Step 1: Write the WebSocket client**

Create `Services\Lobby\LobbyWebSocketClient.cs`:
```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public record RejoinCommand(string LobbyCode, List<ModSetEntry> ModSet, string Region, string RegionIp, int RegionPort);

public class LobbyWebSocketClient
{
    private readonly LauncherConfig _config;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    public event EventHandler<string>? Kicked;
    public event EventHandler<RejoinCommand>? Rejoin;

    public LobbyWebSocketClient(LauncherConfig config) => _config = config;

    public async Task ConnectAsync(string lobbyCode, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var uri = $"{_config.BackendWssUrl}?code={lobbyCode}";
                _ws = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_config.DiscordAccessToken))
                    _ws.Options.SetRequestHeader("Authorization", $"Bearer {_config.DiscordAccessToken}");
                await _ws.ConnectAsync(new Uri(uri), _cts.Token);
                await ReceiveLoopAsync(_ws, _cts.Token);
            }
            catch { }
            finally { _ws?.Dispose(); _ws = null; }
            if (_cts.IsCancellationRequested) break;
            attempt++;
            var delay = Math.Min(5, attempt) * 2000;
            try { await Task.Delay(delay, _cts.Token); } catch { break; }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString() ?? "";

            if (type == "kick")
            {
                var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() : "";
                Kicked?.Invoke(this, reason ?? "");
            }
            else if (type == "rejoin")
            {
                var p = doc.RootElement.GetProperty("payload");
                Rejoin?.Invoke(this, new RejoinCommand(
                    p.GetProperty("lobbyCode").GetString() ?? "",
                    DeserializeMods(p),
                    p.GetProperty("region").GetString() ?? "",
                    p.GetProperty("regionIp").GetString() ?? "",
                    p.GetProperty("regionPort").GetInt32()));
            }
        }
    }

    private static List<ModSetEntry> DeserializeMods(JsonElement p)
    {
        var mods = new List<ModSetEntry>();
        if (p.TryGetProperty("modSet", out var arr))
        {
            foreach (var m in arr.EnumerateArray())
            {
                mods.Add(new ModSetEntry
                {
                    FileName = m.GetProperty("fileName").GetString() ?? "",
                    DownloadUrl = m.GetProperty("downloadUrl").GetString() ?? "",
                    Sha256 = m.TryGetProperty("sha256", out var s) ? s.GetString() : null,
                    Version = m.TryGetProperty("version", out var v) ? v.GetString() : null
                });
            }
        }
        return mods;
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _ws?.Dispose();
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Services/Lobby/LobbyWebSocketClient.cs"
git commit -m "feat: lobby websocket command receiver for kick/rejoin"
```

---

### Task 7: Mod-Set Sync + File-Lock-Safe Install

**Files:**
- Create: `Among Launcher\Services\Lobby\ModSetSync.cs`

**Interfaces:**
- Consumes: `MainWindow.GetModdedPath()`, existing `DownloadModAsync` pattern (file download), `GameProcessManager.KillGame()`, `MainView.StopGame()`.
- Produces:
  - `class ModSetSync` with `Task<List<ModSetEntry>> DiffAsync(List<ModSetEntry> target, CancellationToken ct)` (returns entries whose fileName is missing from `BepInEx\plugins`) and `Task InstallAsync(List<ModSetEntry> missing, IProgress<ModDownloadItem>? progress, CancellationToken ct)` (downloads each to plugins dir with the 5-attempt file-lock retry).

- [ ] **Step 1: Write ModSetSync**

Create `Services\Lobby\ModSetSync.cs`:
```csharp
using AmongLauncher.Models;
using AmongLauncher.Services;

namespace AmongLauncher.Services.Lobby;

public class ModSetSync
{
    private readonly string _pluginsDir;
    private readonly Func<string, string, string, Task> _downloadMod;

    public ModSetSync(string pluginsDir, Func<string, string, string, Task> downloadMod)
    {
        _pluginsDir = pluginsDir;
        _downloadMod = downloadMod;
    }

    public Task<List<ModSetEntry>> DiffAsync(List<ModSetEntry> target, CancellationToken ct)
    {
        Directory.CreateDirectory(_pluginsDir);
        var missing = new List<ModSetEntry>();
        foreach (var entry in target)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_pluginsDir, entry.FileName);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                missing.Add(entry);
        }
        return Task.FromResult(missing);
    }

    public async Task InstallAsync(List<ModSetEntry> missing, IProgress<ModDownloadItem>? progress, CancellationToken ct)
    {
        foreach (var entry in missing)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(_pluginsDir, entry.FileName);
            var item = new ModDownloadItem(entry.DownloadUrl, entry.FileName);
            progress?.Report(item);
            try
            {
                await _downloadMod(entry.FileName, entry.DownloadUrl, dest);
                item.Status = "Installed";
            }
            catch
            {
                item.Status = "Failed";
                throw;
            }
        }
    }
}
```
Where `_downloadMod` is provided by `MainWindow` as a wrapper around its existing `DownloadModAsync` **with the file-lock retry** (Task 9 wires it; the 5-attempt backoff loop lives in the `MainWindow` wrapper or here — put the retry loop in the wrapper in Task 9).

- [ ] **Step 2: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Services/Lobby/ModSetSync.cs"
git commit -m "feat: mod set diff and install helper"
```

---

### Task 8: LobbyJoinService (Join Pipeline)

**Files:**
- Create: `Among Launcher\Services\Lobby\LobbyJoinService.cs`

**Interfaces:**
- Consumes: `LobbyBackendClient.GetLobbyAsync`, `ModSetSync`, `LauncherConfig.ModdedInstallPath`, `GameProcessManager`, `MainView` UI hooks, `PipeServer.BroadcastMessageAsync`.
- Produces:
  - `class LobbyJoinService` with `Task<JoinOutcome> JoinLobbyAsync(string code, CancellationToken ct)` where `JoinOutcome { bool Started; string? Error; }`.
  - Flow: fetch lobby → ensure modded install exists (throw if missing, caller shows setup message) → diff+install mods (kill game first if running) → launch game → wait for `game_ready` (90s, abort on `Process.HasExited`) → send IPC `join_lobby` `{code, region, regionIp, regionPort}` → report.

- [ ] **Step 1: Write the pipeline**

Create `Services\Lobby\LobbyJoinService.cs`:
```csharp
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public record JoinOutcome(bool Started, string? Error);

public class LobbyJoinService
{
    private readonly Func<string, CancellationToken, Task<LobbyInfo?>> _getLobby;
    private readonly Func<List<ModSetEntry>, Task<bool>> _ensureSetup;
    private readonly Func<Task> _killGame;
    private readonly Func<Task> _launchGame;
    private readonly Func<Task<bool>> _waitForGameReady;
    private readonly Func<LobbyInfo, Task> _sendJoinLobby;
    private readonly ModSetSync _modSetSync;

    public LobbyJoinService(
        Func<string, CancellationToken, Task<LobbyInfo?>> getLobby,
        Func<List<ModSetEntry>, Task<bool>> ensureSetup,
        Func<Task> killGame,
        Func<Task> launchGame,
        Func<Task<bool>> waitForGameReady,
        Func<LobbyInfo, Task> sendJoinLobby,
        ModSetSync modSetSync)
    {
        _getLobby = getLobby;
        _ensureSetup = ensureSetup;
        _killGame = killGame;
        _launchGame = launchGame;
        _waitForGameReady = waitForGameReady;
        _sendJoinLobby = sendJoinLobby;
        _modSetSync = modSetSync;
    }

    public async Task<JoinOutcome> JoinLobbyAsync(string code, CancellationToken ct)
    {
        var lobby = await _getLobby(code, ct);
        if (lobby == null)
            return new JoinOutcome(false, "Lobby not found");

        var setupOk = await _ensureSetup(lobby.ModSet);
        if (!setupOk)
            return new JoinOutcome(false, "Modded Among Us is not installed. Run one-click setup first.");

        var missing = await _modSetSync.DiffAsync(lobby.ModSet, ct);
        if (missing.Count > 0)
        {
            await _killGame();
            await _modSetSync.InstallAsync(missing, null, ct);
        }

        await _launchGame();

        var ready = await _waitForGameReady();
        if (!ready)
            return new JoinOutcome(false, "Game did not become ready in time");

        await _sendJoinLobby(lobby);
        return new JoinOutcome(true, null);
    }
}
```

- [ ] **Step 2: Wire implementations in MainWindow**

In `MainWindow.xaml.cs`, add a method that constructs `LobbyJoinService` with real implementations:
- `_getLobby` → `LobbyBackendClient.GetLobbyAsync`
- `_ensureSetup` → checks `GetModdedPath()` non-empty and `winhttp.dll` exists; if not, return `false`
- `_killGame` → `Dispatcher.Invoke(() => mv.StopGame())` + wait for process exit (bounded 15s via `GameProcessManager`)
- `_launchGame` → `Dispatcher.Invoke(() => mv.LaunchGame())`
- `_waitForGameReady` → await a `TaskCompletionSource` set by the `game_ready` handler; cancel after 90s OR on `Process.HasExited`
- `_sendJoinLobby` → `_pipeServer.BroadcastMessageAsync("join_lobby", new { lobby.Code, lobby.Region, lobby.RegionIp, lobby.RegionPort })`
Add a `TaskCompletionSource<bool> _gameReadyTcs` reset before each launch.

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Services/Lobby/LobbyJoinService.cs" "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: lobby join pipeline service"
```

---

### Task 9: MainWindow Wiring — Join Flow, IPC Handlers, File-Lock Retry

**Files:**
- Modify: `Among Launcher\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `LobbyJoinService`, `LobbyBackendClient`, `LobbyWebSocketClient`, `ModSetSync`, `SingleInstance`, `DeepLinkHandler.TryParseJoin`.
- Produces:
  - `public async Task HandleJoinLinkAsync(string code)` — drives the join flow with a busy indicator; on failure shows a status message.
  - IPC handlers registered: `lobby_created` (broadcast payload passthrough → backend `CreateLobbyAsync` + start heartbeat), `lobby_closed` (→ backend `DisbandAsync` + stop heartbeat), `player_joined`/`player_left` (forwarded to backend), `join_lobby_result` (surface errors).
  - `DownloadModAsync` wrapper with the 5-attempt file-lock retry loop.
  - A `_gameReadyTcs` reset per launch and a `game_ready` handler that completes it.

- [ ] **Step 1: Add the file-lock retry wrapper**

Add to `MainWindow.xaml.cs` (next to existing `DownloadModAsync`):
```csharp
private async Task DownloadModWithRetryAsync(string modId, string url, string destPath)
{
    var delays = new[] { 250, 500, 1000, 2000, 4000 };
    for (var i = 0; i < delays.Length; i++)
    {
        try
        {
            await DownloadModAsync(modId, url, destPath);
            return;
        }
        catch (IOException) when (i < delays.Length - 1)
        {
            LogDebug($"[Launcher] File locked, retry {i + 1}/{delays.Length} for {destPath}");
            await Task.Delay(delays[i]);
        }
    }
}
```
Ensure `GetModdedPath()` is accessible and `KillGame` waits: in `_killGame` wrapper call `_gameManager.KillGame()` then `while (_gameManager.IsGameRunning()) await Task.Delay(500);` bounded to 15s.

- [ ] **Step 2: Wire game_ready TCS + crash guard**

Add field `private TaskCompletionSource<bool>? _gameReadyTcs;`. In the `game_ready` handler set `_gameReadyTcs?.TrySetResult(true);` before returning the ack. In `_waitForGameReady`, create a fresh TCS before launch, then:
```csharp
var readyTask = _gameReadyTcs.Task;
var timeout = Task.Delay(90_000, ct);
var exited = Task.Run(async () =>
{
    while (_gameManager.IsGameRunning()) await Task.Delay(500);
    return true;
}, ct);
var done = await Task.WhenAny(readyTask, timeout, exited);
return done == readyTask && await readyTask;
```

- [ ] **Step 3: Register new IPC handlers**

In the ctor, after existing registrations:
```csharp
_pipeServer.RegisterHandler("lobby_created", async element =>
{
    var p = element.GetProperty("payload");
    var info = new LobbyInfo
    {
        Code = p.GetProperty("code").GetString() ?? "",
        Region = p.GetProperty("region").GetString() ?? "",
        RegionIp = p.GetProperty("regionIp").GetString() ?? "",
        RegionPort = p.TryGetProperty("regionPort", out var rp) ? rp.GetInt32() : 22023,
        ModSet = GetInstalledModSet()
    };
    _activeLobby = info;
    await _backend.CreateLobbyAsync(new CreateLobbyRequest(info.Code, info.Region, info.RegionIp, info.RegionPort, info.ModSet, _userId), CancellationToken.None);
    StartHeartbeat(info.Code);
    return new { type = "lobby_created_ack" };
});
_pipeServer.RegisterHandler("lobby_closed", async element =>
{
    var code = element.GetProperty("payload").GetProperty("code").GetString() ?? "";
    if (_activeLobby != null) await _backend.DisbandAsync(code, CancellationToken.None);
    StopHeartbeat();
    _activeLobby = null;
    return new { type = "lobby_closed_ack" };
});
_pipeServer.RegisterHandler("player_joined", ForwardPlayerChange);
_pipeServer.RegisterHandler("player_left", ForwardPlayerChange);
_pipeServer.RegisterHandler("join_lobby_result", async element =>
{
    var p = element.GetProperty("payload");
    var ok = p.GetProperty("success").GetBoolean();
    if (!ok)
        Dispatcher.Invoke(() => mv.UpdateModStatusText($"Join failed: {p.TryGetProperty("error", out var e) ? e.GetString() : "unknown error"}"));
    return null;
});
```
Add helper `List<ModSetEntry> GetInstalledModSet()` scanning `BepInEx\plugins\*.dll` (fileName only, no download url).

- [ ] **Step 4: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: wire lobby IPC handlers, join flow, and file-lock retries"
```

---

### Task 10: LobbyHeartbeatService + Kick/Rejoin Command Handling

**Files:**
- Create: `Among Launcher\Services\Lobby\LobbyHeartbeatService.cs`
- Modify: `Among Launcher\Services\Lobby\LobbyCommandService.cs` (create)

**Interfaces:**
- Consumes: `LobbyBackendClient.HeartbeatAsync/RepostAsync/DisbandAsync/KickAsync`, `LobbyWebSocketClient.Kicked/Rejoin`, `LobbyJoinService`, `ModSetSync`.
- Produces:
  - `class LobbyHeartbeatService` with `Start(string code, string hostUserId)`, `Stop()` (interval 30s; uses a `CancellationTokenSource`).
  - `class LobbyCommandService` — subscribes to WebSocket events: on `Kicked` → `MainView.StopGame()`; on `Rejoin` → kill, install new mod set, relaunch, wait `game_ready`, send `join_lobby`.

- [ ] **Step 1: Write LobbyHeartbeatService**

Create `Services\Lobby\LobbyHeartbeatService.cs`:
```csharp
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class LobbyHeartbeatService
{
    private readonly Func<string, string, CancellationToken, Task<bool>> _heartbeat;
    private CancellationTokenSource? _cts;

    public LobbyHeartbeatService(Func<string, string, CancellationToken, Task<bool>> heartbeat) => _heartbeat = heartbeat;

    public void Start(string code, string hostUserId)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                try { await _heartbeat(code, hostUserId, _cts.Token); } catch { }
            }
        });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
```

- [ ] **Step 2: Write LobbyCommandService**

Create `Services\Lobby\LobbyCommandService.cs`:
```csharp
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class LobbyCommandService
{
    private readonly Func<Task> _killGame;
    private readonly Func<RejoinCommand, Task> _rejoin;

    public LobbyCommandService(LobbyWebSocketClient ws, Func<Task> killGame, Func<RejoinCommand, Task> rejoin)
    {
        _killGame = killGame;
        _rejoin = rejoin;
        ws.Kicked += (_, _) => _ = _killGame();
        ws.Rejoin += async (_, cmd) => await _rejoin(cmd);
    }
}
```
In `MainWindow`, `_rejoin` = same pipeline as `LobbyJoinService` but with the lobby info taken from the `RejoinCommand` (kill → `ModSetSync.InstallAsync` → launch → wait ready → broadcast `join_lobby`).

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Services/Lobby/LobbyHeartbeatService.cs" "Among Launcher/Services/Lobby/LobbyCommandService.cs" "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: lobby heartbeat and kick/rejoin command handling"
```

---

### Task 11: Host Live Control Panel UI

**Files:**
- Create: `Among Launcher\Views\HostControlPanelView.xaml`, `Among Launcher\Views\HostControlPanelView.xaml.cs`
- Modify: `Among Launcher\MainWindow.xaml` (navigation container), `Among Launcher\MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `LobbyInfo` (code, region, ip, port, player count), backend `KickAsync`/`RepostAsync`/`DisbandAsync`, `DiscordUserProfile`.
- Produces: a WPF `UserControl` hosted as the active view while hosting, with a refreshable player list.

- [ ] **Step 1: Create the view XAML**

Create `Views\HostControlPanelView.xaml` with a dark-themed panel:
- A `TextBlock` for the lobby code (large), copy button.
- A region row: `Region`, `RegionIp:RegionPort`.
- A `ListView` `PlayersList` bound to an `ObservableCollection<LobbyPlayer>` with columns `PlayerName`, `Discord`, `IsHost`, and a per-row "Kick" `Button` (command `KickPlayerCommand`).
- Buttons: `RePostButton`, `DisbandButton`.
Use `x:Name` for all controls; code-behind style consistent with `MainView.xaml`.

- [ ] **Step 2: Create the code-behind**

Create `Views\HostControlPanelView.xaml.cs`:
```csharp
using System.Collections.ObjectModel;
using System.Windows;
using AmongLauncher.Models;

namespace AmongLauncher.Views;

public partial class HostControlPanelView : System.Windows.Controls.UserControl
{
    public ObservableCollection<LobbyPlayer> Players { get; } = new();

    public HostControlPanelView(LobbyInfo lobby)
    {
        InitializeComponent();
        CodeText.Text = lobby.Code;
        RegionText.Text = $"{lobby.Region}  {lobby.RegionIp}:{lobby.RegionPort}";
        DataContext = this;
    }

    public void UpdatePlayers(List<LobbyPlayer> players)
    {
        Players.Clear();
        foreach (var p in players) Players.Add(p);
        PlayersCountText.Text = $"{players.Count} players";
    }
}
```
Wire `RePostButton`, `DisbandButton`, and per-row Kick clicks in code-behind with events `event EventHandler? RePostRequested; event EventHandler? DisbandRequested; event EventHandler<string>? KickRequested;`

- [ ] **Step 3: Wire into MainWindow**

In `MainWindow.xaml`, ensure the content area can swap to `HostControlPanelView`. In `MainWindow.xaml.cs`, when `lobby_created` arrives and the user is the host (`_userId == lobby.HostUserId` or the mod flagged host), `Dispatcher.Invoke` to set the content area to the panel; refresh players on `player_joined`/`player_left` (map names to `LobbyPlayer`, resolving Discord tags from a cached backend membership list).

- [ ] **Step 4: Build + manual verify**

Run: `dotnet build "Among Launcher.sln"`
Manual: set a lobby, confirm panel renders code/region and Kick/Re-post/Disband buttons raise the expected backend calls.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Views/HostControlPanelView.xaml" "Among Launcher/Views/HostControlPanelView.xaml.cs" "Among Launcher/MainWindow.xaml" "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: host live control panel UI"
```

---

### Task 12: Mod Profile/Preset Switcher

**Files:**
- Modify: `Among Launcher\Views\SettingsView.xaml`, `Among Launcher\Views\SettingsView.xaml.cs`, `Among Launcher\Views\MainView.xaml`, `Among Launcher\Views\MainView.xaml.cs`
- Create: `Among Launcher\Services\Lobby\ModProfileManager.cs`

**Interfaces:**
- Consumes: `LauncherConfig.Profiles`, `ModSetSync`, `MainView.StopGame/LaunchGame`, `MainView.GetInstalledMods`.
- Produces:
  - `class ModProfileManager` with `SaveProfile(string name, List<ModSetEntry> mods)`, `List<ModProfile> LoadProfiles()`, `DeleteProfile(string name)`.
  - A profile dropdown + "Save current mods as profile" button + "Apply profile" button in `MainView` (reuses the diff+install pipeline; switching kills game if running).

- [ ] **Step 1: Write ModProfileManager**

Create `Services\Lobby\ModProfileManager.cs`:
```csharp
using AmongLauncher.Config;
using AmongLauncher.Models;

namespace AmongLauncher.Services.Lobby;

public class ModProfileManager
{
    private readonly LauncherConfig _config;

    public ModProfileManager(LauncherConfig config) => _config = config;

    public List<ModProfile> LoadProfiles() => _config.Profiles;

    public void SaveProfile(string name, List<ModSetEntry> mods)
    {
        _config.Profiles.RemoveAll(p => p.Name == name);
        _config.Profiles.Add(new ModProfile { Name = name, Mods = mods });
        _config.Save();
    }

    public void DeleteProfile(string name)
    {
        _config.Profiles.RemoveAll(p => p.Name == name);
        _config.Save();
    }
}
```

- [ ] **Step 2: Add UI controls**

In `MainView.xaml`, add a `ComboBox ProfileCombo`, "Save Profile" button, and "Apply Profile" button in the mods section. In `MainView.xaml.cs`:
- On load, populate `ProfileCombo` from `ModProfileManager.LoadProfiles()`.
- "Save Profile" prompts for a name (reuse `ConfirmationModal`/`DownloadModsModal` prompt pattern or a simple `TextInput`), captures `GetInstalledMods()` → `ModSetEntry` list (fileName only) → `SaveProfile`.
- "Apply Profile" takes the selected profile, runs `ModSetSync` diff/install (kill game if running), relaunches via existing `LaunchGame()`.

- [ ] **Step 3: Build + manual verify**

Run: `dotnet build "Among Launcher.sln"`
Manual: save a profile, restart app, apply it, confirm mods install.

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Services/Lobby/ModProfileManager.cs" "Among Launcher/Views/MainView.xaml" "Among Launcher/Views/MainView.xaml.cs" "Among Launcher/Views/SettingsView.xaml" "Among Launcher/Views/SettingsView.xaml.cs"
git commit -m "feat: mod profile save and apply switcher"
```

---

### Task 13: Mod PipeClient Inbound Dispatch

**Files:**
- Modify: `Among API\Services\PipeClient.cs`
- Modify: `Among API\Plugin.cs`

**Interfaces:**
- Consumes: existing `_handlers` dict, `RegisterHandler`, `ListenAsync`.
- Produces:
  - `RegisterHandler(type, Func<JsonElement, Task<object?>>)` actually dispatches: in `ListenAsync`, after response matching and the legacy `restart` branch, look up `_handlers[type]` and invoke; if it returns non-null, serialize as a response frame (echoing the inbound `id`).
  - `Plugin` registers `join_lobby`, `kick` handlers that raise events / call the game-join hooks (Tasks 14-16).

- [ ] **Step 1: Wire dispatch into ListenAsync**

In `ListenAsync`, replace the broadcast-handling block (the `if (msgType == "restart")` branch) with:
```csharp
if (doc.RootElement.TryGetProperty("type", out var typeProp))
{
    var msgType = typeProp.GetString() ?? "";
    if (msgType == "restart")
    {
        _log.LogInfo("[Pipe] Received restart command from launcher.");
        break;
    }

    if (_handlers.TryGetValue(msgType, out var handler))
    {
        try
        {
            var result = await handler(doc.RootElement);
            if (result != null)
            {
                var respId = doc.RootElement.TryGetProperty("id", out var idP) ? idP.GetString() : "";
                var resp = new Dictionary<string, object>
                {
                    ["type"] = msgType + "_ack",
                    ["id"] = respId ?? "",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                if (result is not string) resp["payload"] = result;
                await SendRawAsync(JsonSerializer.Serialize(resp));
            }
        }
        catch (Exception ex)
        {
            _log.LogError($"[Pipe] Handler {msgType} failed: {ex.Message}");
        }
    }
}
```
Add a private `SendRawAsync(string json)` method that writes the length-prefixed frame (reuse the same write logic as `SendMessageAsync`).

- [ ] **Step 2: Expose inbound events on PipeClient**

Add:
```csharp
public event EventHandler<JsonElement>? MessageReceived;
public event EventHandler? JoinLobbyRequested;
public event EventHandler<KickRequestedEventArgs>? KickRequested;

public class KickRequestedEventArgs : EventArgs { public string Reason { get; init; } = ""; }
```
In `ListenAsync` after handler dispatch, raise `MessageReceived` (and specifically `JoinLobbyRequested` when `msgType == "join_lobby"`, `KickRequested` when `msgType == "kick"`).

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: build succeeds (plugin unchanged behavior otherwise).

- [ ] **Step 4: Commit**

```bash
git add "Among API/Services/PipeClient.cs"
git commit -m "feat: mod pipe client inbound handler dispatch"
```

---

### Task 14: Mod Game-State Tracker (Lobby Created / Closed, Player Join / Left)

**Files:**
- Create: `Among API\Services\GameStateTracker.cs`
- Modify: `Among API\Plugin.cs`, `Among API\Among API.csproj` (assembly reference strategy per Task 1)

**Interfaces:**
- Consumes: Task 1 research doc for the exact member names; `PipeClient` events.
- Produces:
  - `class GameStateTracker : IDisposable` with `void Start()` / `void Stop()`, events `LobbyCreated(LobbyInfo)`, `LobbyClosed(string reason)`, `PlayerJoined(PlayerInfo)`, `PlayerLeft(PlayerInfo)`.
  - Emits IPC `lobby_created`, `lobby_closed`, `player_joined`, `player_left` from the game loop with debounce.

- [ ] **Step 1: Implement the tracker per the research doc**

Create `Services\GameStateTracker.cs` following the research doc's hook point. Pattern (adjust names to the research doc):
```csharp
using BepInEx.Logging;

namespace AmongApi.Services;

public record LobbyInfo(string Code, string Region, string RegionIp, int RegionPort);
public record PlayerInfo(string PlayerName, int PlayerCount);

public class GameStateTracker
{
    private readonly ManualLogSource _log;
    private bool _wasInLobby;
    private int _lastPlayerCount = -1;
    private readonly object _lock = new();

    public event EventHandler<LobbyInfo>? LobbyCreated;
    public event EventHandler<string>? LobbyClosed;
    public event EventHandler<PlayerInfo>? PlayerJoined;
    public event EventHandler<PlayerInfo>? PlayerLeft;

    public GameStateTracker(ManualLogSource log) => _log = log;

    public void Start()
    {
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (true)
        {
            try { Tick(); }
            catch { }
            await Task.Delay(500);
        }
    }

    private void Tick()
    {
        // Read current lobby state using the research-doc member names.
        // var inLobby = ...; var code = ...; var count = ...;
        // On transition into lobby -> raise LobbyCreated; out -> LobbyClosed;
        // On player count change -> raise PlayerJoined/PlayerLeft.
    }
}
```
This is the boundary where the game assembly calls live; the concrete calls are filled in from the research doc. If the research doc chose the reflection strategy, load `Assembly-CSharp.dll` from the modded install here instead of compile-time references.

- [ ] **Step 2: Hook tracker events to PipeClient sends**

In `Plugin.cs` `RunAsync`, after connecting:
```csharp
var tracker = new GameStateTracker(Log);
tracker.LobbyCreated += (_, info) => { _lastLobby = info; _ = pipe.SendMessageAsync("lobby_created", info); };
tracker.LobbyClosed += (_, reason) => _ = pipe.SendMessageAsync("lobby_closed", new { code = _lastLobby?.Code ?? "", reason });
tracker.PlayerJoined += (_, p) => _ = pipe.SendMessageAsync("player_joined", p);
tracker.PlayerLeft += (_, p) => _ = pipe.SendMessageAsync("player_left", p);
tracker.Start();
```
Add a plugin field `LobbyInfo? _lastLobby;` updated in the `LobbyCreated` handler.

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: builds per the Task 1 reference strategy.

- [ ] **Step 4: Manual verify**

Launch the modded game, create a lobby, join with a second client. Expected: `AmongApi.log` and launcher IPC log show `lobby_created`, `player_joined`, `player_left`, `lobby_closed` events.

- [ ] **Step 5: Commit**

```bash
git add "Among API/Services/GameStateTracker.cs" "Among API/Plugin.cs" "Among API/Among API.csproj"
git commit -m "feat: in-game lobby and player state tracker"
```

---

### Task 15: Mod Direct Join Handler

**Files:**
- Create: `Among API\Services\LobbyJoiner.cs`
- Modify: `Among API\Plugin.cs`

**Interfaces:**
- Consumes: Task 1 research doc (join + region injection), `PipeClient.JoinLobbyRequested`/`RegisterHandler("join_lobby")`.
- Produces:
  - `class LobbyJoiner` with `Task<JoinResult> JoinAsync(string code, string region, string regionIp, int regionPort)` where `JoinResult { bool Success; string? Error; }`.
  - Calls the game's region registration + `JoinOnlineGame`, returning success/failure to the launcher via `join_lobby_result`.

- [ ] **Step 1: Implement the joiner per the research doc**

Create `Services\LobbyJoiner.cs`. Following the research doc:
```csharp
namespace AmongApi.Services;

public record JoinResult(bool Success, string? Error);

public class LobbyJoiner
{
    public Task<JoinResult> JoinAsync(string code, string region, string regionIp, int regionPort)
    {
        try
        {
            // 1. Register/select the custom region via ServerManager (research doc).
            // 2. Decode code -> int via GameCode (research doc).
            // 3. AmongUsClient.Instance.JoinOnlineGame(...) (research doc signature).
            return Task.FromResult(new JoinResult(true, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new JoinResult(false, ex.Message));
        }
    }
}
```

- [ ] **Step 2: Wire to the IPC handler**

In `Plugin.cs`, register the handler:
```csharp
pipe.RegisterHandler("join_lobby", async element =>
{
    var p = element.GetProperty("payload");
    var code = p.GetProperty("code").GetString() ?? "";
    var region = p.GetProperty("region").GetString() ?? "";
    var regionIp = p.GetProperty("regionIp").GetString() ?? "";
    var regionPort = p.GetProperty("regionPort").GetInt32();
    var result = await joiner.JoinAsync(code, region, regionIp, regionPort);
    return new { success = result.Success, error = result.Error };
});
```
The returned object becomes the `join_lobby_ack` payload (Task 13 dispatch). Also send a `join_lobby_result` message so the launcher's `join_lobby_result` handler sees it if the ack isn't enough.

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: builds per the reference strategy.

- [ ] **Step 4: Manual verify**

With a real lobby + backend stub, click a join link. Expected: launcher sends `join_lobby`; game joins the lobby.

- [ ] **Step 5: Commit**

```bash
git add "Among API/Services/LobbyJoiner.cs" "Among API/Plugin.cs"
git commit -m "feat: in-game direct lobby join handler"
```

---

### Task 16: Mod Host Chat Commands (`/repost`, `/disband`)

**Files:**
- Create: `Among API\Services\ChatCommandHandler.cs`
- Modify: `Among API\Plugin.cs`

**Interfaces:**
- Consumes: `PipeClient` sends, `GameStateTracker.LobbyClosed`.
- Produces:
  - `class ChatCommandHandler` hooked to the game chat send; detects `/repost` and `/disband`:
    - `/repost` → re-send `lobby_created` (launcher re-POSTs to backend).
    - `/disband` → send `lobby_closed { reason: "disband" }` then leave the lobby via the game API.

- [ ] **Step 1: Implement chat command detection**

Create `Services\ChatCommandHandler.cs` following the research doc's chat-send hook (patch the chat send method or poll the chat input). On message:
- If starts with `/repost` → invoke `repost` action.
- If starts with `/disband` → invoke `disband` action, and suppress the raw text from being sent as a normal chat message if possible.

- [ ] **Step 2: Wire actions**

In `Plugin.cs`:
```csharp
var commands = new ChatCommandHandler(Log);
commands.OnRepost = () => _ = pipe.SendMessageAsync("lobby_created", _lastLobby);
commands.OnDisband = () => { _ = pipe.SendMessageAsync("lobby_closed", new { code = _lastLobby?.Code ?? "", reason = "disband" }); LeaveLobby(); };
commands.Start();
```

- [ ] **Step 3: Build**

Run: `dotnet build "Among Launcher.sln"`
Expected: builds.

- [ ] **Step 4: Commit**

```bash
git add "Among API/Services/ChatCommandHandler.cs" "Among API/Plugin.cs"
git commit -m "feat: host chat commands for repost and disband"
```

---

### Task 17: Update api.md

**Files:**
- Modify: `api.md`

**Interfaces:**
- Consumes: all IPC additions from Tasks 8-16.
- Produces: accurate protocol doc.

- [ ] **Step 1: Document new message types**

Add to `api.md`:
- Mod → Launcher: `lobby_created { code, region, regionIp, regionPort }`, `lobby_closed { code, reason? }`, `player_joined { playerName, playerCount }`, `player_left { playerName, playerCount }`, `join_lobby_result { success, error? }`, `game_ready` (now with payload `{ gameVersion, amongApiVersion }` if Task 14 adds it — otherwise note payload-less).
- Launcher → Mod: `join_lobby { code, region, regionIp, regionPort }`, `kick { reason? }`.
- Correct the stale sections flagged by the code review: `mod_status` direction/semantics, `restart_ack` sender, undocumented live types (`launcher_ready`, `mod_status_response`, `install_mod_ack`, `restart_ack` fields).
- Update the response table to match current code.

- [ ] **Step 2: Review against code**

Cross-check every type listed against `MainWindow.xaml.cs` and `PipeServer.cs` handler registrations and the plugin's sends. Expected: no type listed is missing a live implementation (or is explicitly marked as reserved/legacy).

- [ ] **Step 3: Commit**

```bash
git add api.md
git commit -m "docs: update IPC protocol for lobby join and management"
```

---

### Task 18: End-to-End Manual Test + CI Check

**Files:**
- Modify: `.github/workflows/build.yml` (only if Task 1's reference strategy requires it)
- Test: manual end-to-end

**Interfaces:**
- Consumes: everything.
- Produces: verified working flow.

- [ ] **Step 1: Build both projects**

Run: `dotnet build "Among Launcher.sln" -c Release`
Expected: succeeds.

- [ ] **Step 2: Manual end-to-end — host**

1. Host opens launcher, runs one-click setup, adds mods, launches game, creates a lobby.
2. Expected: mod emits `lobby_created`; launcher `POST /lobby`; backend stores; bot posts embed with Join button and `0/N` players; launcher shows the Host Control Panel with the code.

- [ ] **Step 3: Manual end-to-end — joiner**

1. On a second machine, click the Discord Join link.
2. Expected: launcher opens (single instance), `GET /lobby/{code}`, sets up if needed, installs missing mods, launches, waits `game_ready`, sends `join_lobby`, mod joins the lobby; bot embed updates to `N/15`.

- [ ] **Step 4: Manual end-to-end — kick/repost/disband**

1. Host clicks Re-post → bot embed refreshed.
2. Host clicks Kick on a player → backend pushes `kick` → that launcher kills the game; embed decrements.
3. Host runs `/disband` (or clicks Disband) → `lobby_closed` → embed removed.

- [ ] **Step 5: Manual end-to-end — mod change reconnect**

1. Host switches a mod profile, relaunches, creates a new lobby.
2. Expected: backend sees new mod set → pushes `rejoin` to previously connected launchers → they kill, install new mods, relaunch, rejoin.

- [ ] **Step 6: CI sanity**

If Task 1 chose the conditional-`<Reference>` strategy, verify `build.yml` still builds the plugin without game files (the reference must be conditional on file existence). Commit any CI change with:
```bash
git add .github/workflows/build.yml
git commit -m "ci: keep plugin build game-assembly independent"
```

- [ ] **Step 7: Final commit**

Ensure `api.md` and code are consistent; commit any remaining changes:
```bash
git add -A
git commit -m "chore: final end-to-end verification fixes"
```

---

## Self-Review Notes

**Spec coverage:** every spec section maps to a task — deep link (3,4), join pipeline (8,9), backend client (5), WebSocket (6), heartbeat/kick/rejoin (10), host panel (11), profiles (12), mod in-game hooks (14-16), IPC dispatch fix (13), file-lock/retry + boot crash guard (9), single-instance (4), custom region injection (1,15), api.md (17), end-to-end (18).

**Known deferred items:** backend REST/WS server implementation and the Discord bot embed are user-owned; the launcher/mod only implement the client contract. The "launcher closed but game running" kick gap is accepted (spec Open Decisions) — heartbeat expiry covers the host side.
