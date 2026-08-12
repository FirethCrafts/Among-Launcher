# Launcher Spec Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the Among Launcher and Among API to strictly adhere to the Launcher Spec, Backend Spec, and IPC Protocol contracts.

**Architecture:** The changes are concentrated in three areas: (1) mod sync now uses SHA-256 hashing for diff and validation, (2) the host uploads missing mods to the backend before creating a lobby, (3) process kill timeout increased to 15s. The IPC and deep-link layers are already spec-compliant.

**Tech Stack:** C# (.NET 10), WPF, Windows Named Pipes, BepInEx 6 IL2CPP, System.Text.Json, System.Security.Cryptography (SHA256)

## Global Constraints

- Windows Named Pipe `\\.\pipe\AmongLauncher.IPC`, bidirectional `Byte` mode, single-client sequential.
- Frame: 4-byte LE length prefix + UTF-8 JSON, max 1 MB.
- Envelope: `{ "type": string, "id": string (8-hex), "timestamp": long, "payload": object }`.
- Backend endpoints under `/api/v1/lobbies/` and `/api/v1/mods/`.
- All `.dll` file I/O wrapped in 5-attempt retry with exponential backoff (250ms → 4s).
- Before modifying `.dll` files, check if Among Us is running and kill with 15s wait.

---

### Task 1: Add SHA-256 utility

**Files:**
- Create: `Among Launcher/Services/Sha256Helper.cs`

**Interfaces:**
- Produces: `Sha256Helper.HashFileAsync(string path) -> string` (returns hex string)

- [ ] **Step 1: Create the helper**

```csharp
using System.Security.Cryptography;

namespace AmongLauncher.Services;

public static class Sha256Helper
{
    public static async Task<string> HashFileAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Services/Sha256Helper.cs"
git commit -m "feat: add SHA-256 file hashing utility"
```

---

### Task 2: Add hash fields to ModSetEntry

**Files:**
- Modify: `Among Launcher/Models/ModSetEntry.cs`

**Interfaces:**
- Consumes: `Sha256Helper` (from Task 1)
- Produces: `ModSetEntry` with `Sha256` property (already exists, verify it's used)

- [ ] **Step 1: Verify ModSetEntry has Sha256**

Read `Among Launcher/Models/ModSetEntry.cs`. It should already have:
```csharp
public class ModSetEntry
{
    public string FileName { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public string? Version { get; set; }
}
```

If `Sha256` is missing, add it. If present, proceed.

- [ ] **Step 2: Commit (if changed)**

```bash
git add "Among Launcher/Models/ModSetEntry.cs"
git commit -m "feat: ensure ModSetEntry has Sha256 field"
```

---

### Task 3: Compute SHA-256 hashes in GetInstalledModSet

**Files:**
- Modify: `Among Launcher/MainWindow.xaml.cs` (the `GetInstalledModSet` method)

**Interfaces:**
- Consumes: `Sha256Helper.HashFileAsync`
- Produces: `ModSetEntry` list with `Sha256` populated

- [ ] **Step 1: Update GetInstalledModSet to compute hashes**

Find the `GetInstalledModSet` method and change it to:

```csharp
private async Task<List<ModSetEntry>> GetInstalledModSetAsync()
{
    var moddedPath = GetModdedPath();
    if (string.IsNullOrEmpty(moddedPath)) return new List<ModSetEntry>();

    var pluginsDir = Path.Combine(moddedPath, "BepInEx", "plugins");
    if (!Directory.Exists(pluginsDir)) return new List<ModSetEntry>();

    var entries = new List<ModSetEntry>();
    foreach (var file in Directory.GetFiles(pluginsDir, "*.dll"))
    {
        var hash = await Sha256Helper.HashFileAsync(file);
        entries.Add(new ModSetEntry { FileName = Path.GetFileName(file), Sha256 = hash });
    }
    return entries;
}
```

- [ ] **Step 2: Update the lobby_created handler to use the async version**

Find the `lobby_created` handler where `GetInstalledModSet()` is called. Change to `await GetInstalledModSetAsync()`.

- [ ] **Step 3: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: compute SHA-256 hashes for installed mods"
```

---

### Task 4: Upload missing mods to backend on lobby create

**Files:**
- Modify: `Among Launcher/Services/Lobby/LobbyBackendClient.cs`

**Interfaces:**
- Consumes: `ModSetEntry` with `FileName` and `Sha256`
- Produces: `UploadModAsync(Stream file, string name) -> ModInfoEntry?`

- [ ] **Step 1: Add UploadModAsync method**

Add to `LobbyBackendClient`:

```csharp
public async Task<ModInfoEntry?> UploadModAsync(Stream fileStream, string fileName, CancellationToken ct)
{
    try
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StreamContent(fileStream), "file", fileName);
        content.Add(new StringContent(Path.GetFileNameWithoutExtension(fileName)), "name");

        using var msg = new HttpRequestMessage(HttpMethod.Post, "api/v1/mods") { Content = content };
        ApplyAuth(msg);
        using var resp = await _http.SendAsync(msg, ct);
        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync<ModInfoEntry>(cancellationToken: ct);
    }
    catch { return null; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Services/Lobby/LobbyBackendClient.cs"
git commit -m "feat: add mod upload endpoint to backend client"
```

---

### Task 5: Update lobby creation to upload mods and pass file_hash

**Files:**
- Modify: `Among Launcher/MainWindow.xaml.cs` (lobby_created handler)

**Interfaces:**
- Consumes: `LobbyBackendClient.UploadModAsync`, `GetInstalledModSetAsync`

- [ ] **Step 1: Update lobby_created handler**

After computing `modInfoEntries`, upload any mods that don't have a `FileHash` to the backend. Replace the lobby creation section:

```csharp
var modEntries = new List<ModInfoEntry>();
foreach (var entry in info.ModSet)
{
    var filePath = Path.Combine(GetModdedPath()!, "BepInEx", "plugins", entry.FileName);
    if (!File.Exists(filePath)) continue;

    if (string.IsNullOrEmpty(entry.Sha256))
        entry.Sha256 = await Sha256Helper.HashFileAsync(filePath);

    var uploaded = await _backend.UploadModAsync(
        File.OpenRead(filePath), entry.FileName, CancellationToken.None);

    modEntries.Add(uploaded ?? new ModInfoEntry(entry.FileName, entry.Version, entry.Sha256));
}

await _backend.CreateLobbyAsync(
    new CreateLobbyRequest(info.Code, info.Region, info.Host, "modded", modEntries),
    CancellationToken.None);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: upload mods to backend and pass file_hash on lobby create"
```

---

### Task 6: SHA-256 based mod diff in ModSetSync

**Files:**
- Modify: `Among Launcher/Services/Lobby/ModSetSync.cs`

**Interfaces:**
- Consumes: `Sha256Helper.HashFileAsync`
- Produces: `DiffAsync` now compares SHA-256 hashes, not just file existence

- [ ] **Step 1: Rewrite DiffAsync to use SHA-256**

```csharp
public async Task<List<ModSetEntry>> DiffAsync(List<ModSetEntry> target, CancellationToken ct)
{
    Directory.CreateDirectory(_pluginsDir);
    var missing = new List<ModSetEntry>();

    foreach (var entry in target)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(_pluginsDir, entry.FileName);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            missing.Add(entry);
            continue;
        }

        if (!string.IsNullOrEmpty(entry.Sha256))
        {
            var localHash = await Sha256Helper.HashFileAsync(path);
            if (!string.Equals(localHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                missing.Add(entry);
        }
    }

    return missing;
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Services/Lobby/ModSetSync.cs"
git commit -m "feat: SHA-256 based mod diff in ModSetSync"
```

---

### Task 7: Download mods from backend and validate hash

**Files:**
- Modify: `Among Launcher/Services/Lobby/ModSetSync.cs`
- Modify: `Among Launcher/Services/Lobby/LobbyBackendClient.cs`

**Interfaces:**
- Consumes: `ModInfoEntry` with `Id` and `FileHash` from backend response
- Produces: Downloads via `GET /api/v1/mods/{id}/download`, validates SHA-256

- [ ] **Step 1: Add GetModDownloadUrl to LobbyBackendClient**

```csharp
public string GetModDownloadUrl(string modId) => $"api/v1/mods/{modId}/download";
```

- [ ] **Step 2: Update ModSetSync to download from backend**

Change the constructor to accept a `LobbyBackendClient` and update `InstallAsync`:

```csharp
public class ModSetSync
{
    private readonly string _pluginsDir;
    private readonly HttpClient _http;
    private readonly LobbyBackendClient _backend;

    public ModSetSync(string pluginsDir, HttpClient http, LobbyBackendClient backend)
    {
        _pluginsDir = pluginsDir;
        _http = http;
        _backend = backend;
    }

    public async Task<List<ModSetEntry>> DiffAsync(List<ModSetEntry> target, CancellationToken ct)
    {
        // ... (from Task 6)
    }

    public async Task InstallAsync(List<ModSetEntry> missing, CancellationToken ct)
    {
        foreach (var entry in missing)
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(_pluginsDir, entry.FileName);

            if (string.IsNullOrEmpty(entry.DownloadUrl))
                continue;

            var url = entry.DownloadUrl.StartsWith("http")
                ? entry.DownloadUrl
                : _backend.GetModDownloadUrl(entry.DownloadUrl);

            await ModDownloader.DownloadToFileAsync(_http, url, dest);

            if (!string.IsNullOrEmpty(entry.Sha256))
            {
                var hash = await Sha256Helper.HashFileAsync(dest);
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(dest);
                    throw new InvalidOperationException(
                        $"SHA-256 mismatch for {entry.FileName}: expected {entry.Sha256}, got {hash}");
                }
            }
        }
    }
}
```

- [ ] **Step 3: Update callers of ModSetSync constructor**

In `MainWindow.xaml.cs` `JoinPipelineAsync`, change:
```csharp
var modSetSync = new Services.Lobby.ModSetSync(
    Path.Combine(moddedPath, "BepInEx", "plugins"),
    _httpClient, _backend);
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Services/Lobby/ModSetSync.cs" "Among Launcher/Services/Lobby/LobbyBackendClient.cs" "Among Launcher/MainWindow.xaml.cs"
git commit -m "feat: download mods from backend with SHA-256 validation"
```

---

### Task 8: Increase game kill timeout to 15 seconds

**Files:**
- Modify: `Among Launcher/Game/GameProcessManager.cs`

**Interfaces:**
- Produces: `KillGame()` waits up to 15s for process exit

- [ ] **Step 1: Update WaitForExit timeout**

Change both `WaitForExit` calls in `KillGame()`:

```csharp
public void KillGame()
{
    if (_gameProcess == null || _gameProcess.HasExited)
        return;

    try
    {
        if (_gameProcess.CloseMainWindow())
            _gameProcess.WaitForExit(15000);
    }
    catch { }

    if (!_gameProcess.HasExited)
    {
        try
        {
            _gameProcess.Kill();
            _gameProcess.WaitForExit(15000);
        }
        catch { }
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build "Among Launcher/Among Launcher.csproj" --nologo -v q`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Game/GameProcessManager.cs"
git commit -m "feat: increase game kill timeout to 15 seconds"
```

---

### Task 9: Verify IPC handshake is spec-compliant

**Files:**
- Read-only check: `Among Launcher/Ipc/PipeServer.cs`
- Read-only check: `Among API/Services/PipeClient.cs`
- Read-only check: `Among Launcher/MainWindow.xaml.cs` (handler registrations)

**Verification checklist:**
- [ ] Pipe name is `AmongLauncher.IPC`
- [ ] Transmission mode is `Byte`
- [ ] Max instances is `1`
- [ ] Frame format: 4-byte LE length prefix + UTF-8 JSON
- [ ] Max payload: 1 MB
- [ ] Envelope has `type`, `id` (8-hex), `timestamp`, `payload`
- [ ] Launcher handles `game_ready` and replies with `{ "type": "game_ready_ack", "restart": false }`
- [ ] Launcher handles `lobby_created`, `lobby_closed`, `player_joined`, `player_left`, `join_lobby_result`
- [ ] Mod sends `game_ready` immediately after connect
- [ ] Mod sends `lobby_created`, `lobby_closed`, `player_joined`, `player_left` events
- [ ] Mod handles `join_lobby` and sends `join_lobby_result`

- [ ] **Step 1: Verify all items above are true** (they should already be — the IPC layer is spec-compliant)

- [ ] **Step 2: No commit needed if all pass**

---

### Task 10: Verify deep-link & single-instance is spec-compliant

**Files:**
- Read-only check: `Among Launcher/Services/DeepLinkHandler.cs`
- Read-only check: `Among Launcher/Services/SingleInstance.cs`
- Read-only check: `Among Launcher/App.xaml.cs`

**Verification checklist:**
- [ ] Registers `amongus-launcher://` and `amonglauncher://` schemes
- [ ] Single-instance mutex: `Global\AmongLauncher.SingleInstance`
- [ ] Redirect pipe: `AmongLauncher.Redirect`
- [ ] Secondary instances write `args[0]` as UTF-8 line within 2s
- [ ] Primary dispatches via `App.DeepLinkReceived` → `MainWindow.HandleDeepLink`

- [ ] **Step 1: Verify all items above are true**

- [ ] **Step 2: No commit needed if all pass**

---

### Task 11: Verify host control panel & WebSocket controls

**Files:**
- Read-only check: `Among Launcher/Services/Lobby/LobbyHeartbeatService.cs`
- Read-only check: `Among Launcher/Services/Lobby/LobbyWebSocketClient.cs`
- Read-only check: `Among Launcher/Views/HostControlPanelView.xaml.cs`

**Verification checklist:**
- [ ] Heartbeat sends `POST /api/v1/lobbies/{code}/heartbeat` every 30s
- [ ] Repost sends `POST /api/v1/lobbies/{code}/repost`
- [ ] Kick sends `POST /api/v1/lobbies/{code}/kick` with `player_id`
- [ ] Disband sends `DELETE /api/v1/lobbies/{code}`
- [ ] WebSocket connects to `WS /api/v1/ws/{code}?client_id={id}`
- [ ] WebSocket handles `kick` and `disband`/`rejoin` events

- [ ] **Step 1: Verify all items above are true**

- [ ] **Step 2: No commit needed if all pass**

---

### Task 12: Final build verification

- [ ] **Step 1: Full solution build**

Run: `dotnet build "Among Launcher.sln" --nologo -v q`
Expected: 0 errors across all projects

- [ ] **Step 2: Commit all remaining changes**

```bash
git add -A
git commit -m "refactor: align launcher and mod with spec contracts"
```

- [ ] **Step 3: Push**

```bash
git push
```
