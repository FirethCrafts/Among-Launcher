# AmongApi Mod Fix — Implementation Plan

> For approach B: replace the 30s hardcoded sleep with a proper readiness
> detection poll, stabilize the fragile reflection paths (LocalPlayerName,
> MaxPlayers), and clean up the verbose debug logging.

## Context

- AmongApi is a BepInEx IL2CPP plugin that talks to the launcher over a named
  pipe. It detects lobby creation via reflection polling (500ms), sends IPC to
  the launcher, and can auto-post to the backend.
- The 30s sleep (`Plugin.cs:83`) exists because detecting "game fully loaded" is
  broken — the original detection (InnerSloth logo disappearing) fired too early,
  crashing the game by joining a lobby while the network was still connecting.
- `LocalPlayerName()` has 100+ lines of cascading fallbacks that spam the log.
- `MaxPlayers()` has 6 separate code paths trying different object hierarchies.

## Files to modify

| File | Changes |
|------|---------|
| `Among API/Plugin.cs` | Replace 30s sleep with readiness poll, gate tracker/commands |
| `Among API/Services/GameAssembly.cs` | Simplify LocalPlayerName, reduce logging |
| `Among API/Services/GameStateTracker.cs` | Simplify MaxPlayers, reduce logging |

---

## Task 1: Replace the 30s sleep with a readiness poll

**What it does:** Waits for the game to be fully loaded and connected before
the plugin starts its IPC handshake with the launcher.

**File:** `Among API/Plugin.cs`

**Readiness signal:**
- `AmongUsClient.Instance != null` — game object exists
- `GameState == InnerNetClient.GameStates.NotJoined` — at main menu (not in lobby)
- **Stable for 2+ consecutive ticks at 500ms each** — proves the game has
  finished connecting and isn't in a transient state
- **Minimum floor of 8s** — prevents the original crash (joining lobby during login)
- **Hard timeout of 30s** — if game never becomes ready, proceed anyway (current
  behavior, but now as a safety net, not the primary wait)

**Concrete changes to `Plugin.RunAsync()`:**

Replace the `await Task.Delay(30000)` block (lines 83-85) with a call to a
new `WaitForGameReadyAsync()` method:

```csharp
// BEFORE:
FileLogger.Info("Waiting 30 seconds for game to fully load...");
await Task.Delay(30000);
FileLogger.Info("Done waiting.");

// AFTER:
FileLogger.Info("Waiting for game to be ready...");
if (!await WaitForGameReadyAsync())
{
    FileLogger.Warn("Game did not reach ready state; proceeding anyway.");
}
```

**New method in `Plugin.cs`:**

```csharp
private static async Task<bool> WaitForGameReadyAsync()
{
    const int PollIntervalMs = 500;
    const int MinReadyTicks = 3;          // 3 × 500ms = 1.5s of stable NotJoined
    const int MinWaitMs = 8_000;          // minimum time from plugin load
    const int TimeoutMs = 30_000;

    // Resolve the type + nested enum ONCE before the loop (not per-tick).
    var gameStateEnum = GameAssembly.Type("InnerNet.InnerNetClient")?.GetNestedType("GameStates");
    var notJoined = gameStateEnum != null ? GameAssembly.EnumValue(gameStateEnum, "NotJoined") : null;

    var startTime = DateTime.UtcNow;
    int stableTicks = 0;

    while ((DateTime.UtcNow - startTime).TotalMilliseconds < TimeoutMs)
    {
        await Task.Delay(PollIntervalMs);

        double elapsedMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
        if (elapsedMs < MinWaitMs)
            continue;  // still within the minimum wait floor

        var client = GameAssembly.AmongUsClient();
        if (client == null) continue;

        if (notJoined == null) continue; // type not found — skip this tick

        var state = GameAssembly.GetInstanceProp(client, "GameState");

        if (GameAssembly.EnumEquals(state, notJoined))
        {
            stableTicks++;
            if (stableTicks >= MinReadyTicks)
            {
                FileLogger.Info($"Game ready after {(int)elapsedMs}ms ({stableTicks} stable ticks).");
                return true;
            }
        }
        else
        {
            stableTicks = 0;  // reset on any non-NotJoined state
        }
    }

    FileLogger.Warn("Game readiness poll timed out.");
    return false;
}
```

**Why these thresholds:**
- 500ms poll matches the existing GameStateTracker poll rate.
- 3 stable ticks (1.5s) prevents false positives from a single stale tick.
- 8s minimum floor covers the worst-case splash → main menu transition.
- 30s hard timeout is the current behavior as a fallback — no regression.

---

## Task 2: Simplify LocalPlayerName()

**File:** `Among API/Services/GameAssembly.cs`

**Current:** 100+ lines with 6 fallback paths + DebugObjectProperties calls.

**Replace with:** 3 reliable paths, no debug spam:

```csharp
public static string LocalPlayerName()
{
    // Path 1: PlayerControl.LocalPlayer.Data.PlayerName (standard Among Us path)
    try
    {
        var playerControlType = Type("PlayerControl");
        var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
        if (localPlayer != null)
        {
            var data = GetInstanceProp(localPlayer, "Data");
            if (data != null)
            {
                var name = ToStr(GetInstanceProp(data, "PlayerName"));
                if (!string.IsNullOrEmpty(name) && name != "UNKNOWN")
                    return name;
            }
        }
    }
    catch { }

    // Path 2: Direct PlayerName on PlayerControl
    try
    {
        var playerControlType = Type("PlayerControl");
        var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
        if (localPlayer != null)
        {
            var name = ToStr(GetInstanceProp(localPlayer, "PlayerName"));
            if (!string.IsNullOrEmpty(name) && name != "UNKNOWN")
                return name;
        }
    }
    catch { }

    // Path 3: GameData.Players lookup by PlayerId
    try
    {
        var playerControlType = Type("PlayerControl");
        var localPlayer = playerControlType != null ? GetStaticMember(playerControlType, "LocalPlayer") : null;
        if (localPlayer != null)
        {
            var playerId = ToInt(GetInstanceProp(localPlayer, "PlayerId"));
            if (playerId > 0)
            {
                var name = TryGetPlayerNameById(playerId);
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
    }
    catch { }

    FileLogger.Warn("[GameAssembly] LocalPlayerName: all attempts failed, returning UNKNOWN");
    return "UNKNOWN";
}
```

**Removed:** Unity `name` property, `AmongUsClient.GetPlayerName()`, `DebugObjectProperties`, `TryGetLocalPlayerFromGameData()` — all redundant and logging-heavy.

---

## Task 3: Simplify MaxPlayers()

**File:** `Among API/Services/GameStateTracker.cs`

**Current:** 6 paths (GameHostOpts → GameOptions → NormalOptions → PlayerControl.GameOptions → GameData.Instance → client.MaxPlayers).

**Replace with 2 reliable paths:**

```csharp
private static int MaxPlayers()
{
    var client = GameAssembly.AmongUsClient();
    if (client == null) return 15;

    // Path 1: AmongUsClient.GameHostOpts.MaxPlayers (modern Among Us)
    var gameHostOpts = GameAssembly.GetInstanceMember(client, "GameHostOpts");
    if (gameHostOpts != null)
    {
        var max = GameAssembly.ToInt(GameAssembly.GetInstanceMember(gameHostOpts, "MaxPlayers"));
        if (max > 0) return max;
    }

    // Path 2: GameData.Instance.MaxPlayers (fallback for older versions)
    var gameDataType = GameAssembly.Type("GameData");
    var instance = gameDataType != null ? GameAssembly.GetStaticProp(gameDataType, "Instance") : null;
    if (instance != null)
    {
        var max = GameAssembly.ToInt(GameAssembly.GetInstanceMember(instance, "MaxPlayers"));
        if (max > 0) return max;
    }

    return 15; // default
}
```

**Removed:** `NormalOptions`, `PlayerControl.GameOptions`, `PlayerControl.Data.GameOptions`,
`GameData.Instance.MaxPlayerCount`, `client.MaxPlayers`, `ReadMaxPlayersFromOptions()` helper — all dead code paths from older Among Us versions.

**Note:** Once Task 2 simplifies `LocalPlayerName()`, the private helpers `TryGetLocalPlayerFromGameData()` and `TryGetPlayerNameById()` may also become dead code (only called from the removed `PlayerId` path). Confirm after Task 2 lands and remove if so.

---

## Task 4: Clean up debug logging

**File:** `Among API/Services/GameAssembly.cs` and `Among API/Services/GameStateTracker.cs`

**Changes:**
- Remove `DebugObjectProperties` calls in `LocalPlayerName()` (already removed in Task 2).
- Remove `DebugObjectProperties` calls in `TryGetLocalPlayerFromGameData()` (lines 600-613) and `TryGetPlayerNameById()` (lines 636-668) — these are hot-path helpers that should not dump full object graphs during normal operation.
- Remove the per-player logging in `GetAllPlayerLevels` and `GetAllPlayerPings` — keep only start/done/exception logs.
- Remove verbose `LocalPlayerName` debug output (lines 482-577 in GameAssembly.cs already simplified in Task 2).

**Specifically in `GameStateTracker.GetAllPlayerLevels()` and `GetAllPlayerPings()`:**

Replace the inner per-player `FileLogger.Info` calls with just keeping the start/done/exception logs.

---

## Verification

1. Build the AmongApi project (dotnet build) — confirm no compile errors.
2. Install the built DLL into a modded Among Us copy.
3. Launch via the launcher → verify the game is detected as ready WITHOUT a 30s blank wait (should take 8-15s instead).
4. Host a lobby → verify `lobby_created` IPC fires with correct host name (not "UNKNOWN") and correct max players.
5. Check `BepInEx/AmongApi.log` — should have minimal per-player logging (not 100+ debug lines).
