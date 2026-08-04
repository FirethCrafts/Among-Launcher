# Lobby-Created Payload Enrichment (Region & Host) — Spec

**Date:** 2026-08-03
**Status:** Implemented (commit `3ade75c`)

## Summary

The `Among API` BepInEx plugin now resolves the active region's display name and the
host's display name from the game's runtime memory (via reflection) and includes them
in the `lobby_created` IPC frame sent to the launcher over the named pipe. Previously
the plugin sent `region`/`regionIp` empty and omitted `host` entirely.

## Motivation

The launcher mirrors `lobby_created` to the self-hosted backend and shows the Host
Control Panel. The panel and the backend invite embed were showing an empty region
("") and an unknown host, because the plugin never read them from the game. This
change populates both fields so the backend, the Discord embed, and the host panel
display real values.

## Scope

- `Among API\Services\GameAssembly.cs` — two new reflection helpers.
- `Among API\Services\GameStateTracker.cs` — `LobbyInfo` extended; poll loop resolves
  the new fields on each tick.
- `Among API\Plugin.cs` — `lobby_created` and `/repost` payloads include `host` and
  `playerCount`.
- `api.md` — documented the enriched payload.

The launcher (`Among Launcher\`) required **no changes**: its `lobby_created` handler
reads specific keys (`code`, `region`, `regionIp`, `regionPort`) via `GetProperty` and
safely ignores the new `host`/`playerCount` fields. `region` now carries the real
region name and flows through to `POST /lobby` and the host panel.

## How It Works

### 1. Reflection helpers (`GameAssembly.cs`)

Both helpers are null-safe and never throw; on any resolution failure they log a
warning and return `"UNKNOWN"`.

**`CurrentRegionName()`** — resolves the selected region's display label:
1. Resolve the `ServerManager` type; its singleton is inherited, so read `Instance`
   from the base type: `ServerManagerType.BaseType` (the closed generic
   `DestroyableSingleton<ServerManager>`), via `GetStaticProp(baseType, "Instance")`.
   This mirrors the proven pattern already used by `LobbyJoiner`.
2. Read `CurrentRegion` (an instance property on `ServerManager`).
3. Read `Name` (a property on `IRegionInfo`).
4. Return the name; if empty/absent return `"UNKNOWN"`.

**`LocalPlayerName()`** — resolves the local player's display name:
1. Resolve the `PlayerControl` type and read the static field `LocalPlayer`
   (`GetStaticMember(type, "LocalPlayer")`).
2. Read `Data` (an instance property returning `NetworkedPlayerInfo`).
3. Read `PlayerName` (an instance property).
4. Return the name; if empty/absent return `"UNKNOWN"`.

### 2. Game state tracker (`GameStateTracker.cs`)

- `LobbyInfo` record extended from
  `(Code, Region, RegionIp, RegionPort)` to
  `(Code, Region, RegionIp, RegionPort, Host, PlayerCount)`.
- The 500 ms `Tick()` poll reads `region` and `host` alongside the existing
  `code`/`count`/`isHost` reads, inside the same try/catch (a read failure logs and
  aborts the tick — no exception escapes).
- On the host-only lobby-created transition the tracker raises
  `LobbyCreated(new LobbyInfo(code, region, "", 0, host, count))`.
- `playerCount` is the count read in the same tick (seeded before the event fires).

### 3. IPC payload (`Plugin.cs`)

The `lobby_created` frame now sends:

```json
{
  "type": "lobby_created",
  "id": "u1v2w3x4",
  "timestamp": 1735689600000,
  "payload": {
    "code": "ABCDEF",
    "region": "NA",
    "regionIp": "127.0.0.1",
    "regionPort": 22023,
    "host": "PlayerName",
    "playerCount": 1
  }
}
```

`regionIp`/`regionPort` remain empty/`0` (the launcher falls back to port `22023` for
non-positive `regionPort`). The `/repost` chat command rebuilds the same payload from
the cached `_lastLobby`, so it also carries `host`/`playerCount`.

## Verified Member Names (installed interop assembly)

Confirmed against `BepInEx\interop\Assembly-CSharp.dll` via `ilspycmd`:

| Member | Kind | Result |
|--------|------|--------|
| `ServerManager : DestroyableSingleton<ServerManager>` | class | ✅ verified |
| `DestroyableSingleton<ServerManager>.Instance` | static prop (base type) | ✅ verified |
| `ServerManager.CurrentRegion` | instance property → `IRegionInfo` | ✅ verified |
| `IRegionInfo.Name` | instance property | ✅ verified |
| `PlayerControl.LocalPlayer` | static field | ✅ verified |
| `PlayerControl.Data` | instance property → `NetworkedPlayerInfo` | ✅ verified |
| `NetworkedPlayerInfo.PlayerName` | instance property | ✅ verified |

Note: `SaveManager.PlayerName` does **not** exist in this game build; the
`PlayerControl.LocalPlayer.Data.PlayerName` path is used instead.

## Error Handling / Fallbacks

- Every reflection step in both helpers is wrapped; failures log once via
  `GameAssembly.Log` and return `"UNKNOWN"`.
- `ServerManager` may be unavailable before the game finishes booting; the tracker
  simply reports `"UNKNOWN"` for region until it loads. The launcher tolerates it.
- The tracker's `Tick()` wraps all reads in one try/catch, so a reflection hiccup
  cannot crash the poll loop or the plugin.

## Testing

- Build: `dotnet build "Among Launcher.sln"` — 0 errors (both projects).
- Reflection members verified against the installed interop assembly (table above).
- Launcher compatibility: the `lobby_created` handler reads only the keys it knows;
  extra `host`/`playerCount` fields are ignored (verified by reading
  `MainWindow.xaml.cs` handler).
- In-game runtime verification of the actual emitted values still requires a live
  game session + launcher (manual end-to-end); compile and member-resolution are
  verified.

## Files Changed

| File | Change |
|------|--------|
| `Among API\Services\GameAssembly.cs` | Added `CurrentRegionName()`, `LocalPlayerName()` |
| `Among API\Services\GameStateTracker.cs` | Extended `LobbyInfo`, resolve region/host in `Tick()` |
| `Among API\Plugin.cs` | `lobby_created` + `/repost` payloads include `host`, `playerCount` |
| `api.md` | Documented enriched `lobby_created` payload |

Commit: `3ade75c` — `feat: resolve region and host name into lobby_created payload`
