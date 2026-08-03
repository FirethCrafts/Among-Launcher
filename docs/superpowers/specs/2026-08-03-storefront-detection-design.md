# Storefront Detection & Unavailable-Install Popup — Design

**Date:** 2026-08-03
**Status:** Approved (Approach A — Dedicated modal + presence checks)

## Overview

Improve storefront detection for Epic and Xbox/Microsoft Store Among Us installs,
and — when the launcher has strong evidence an install exists but cannot be used
(a locked `WindowsApps` folder, or a manifest references Among Us but no folder
resolves) — show a dedicated popup with a short plain-language explanation, a
short "what to do" answer, and a link to the adaptation guide at the bottom.

**Non-goals:**
- No attempt to make MS Store / Xbox installs fully work outside their package —
  that's out of scope; the popup explains the limitation.
- No new NuGet packages.
- No change to the Steam detection path behavior.

## Architecture

### New: `GameSearchResult` record

Replace the `(string? Path, Storefront? Storefront)` tuple returned by
`GameFinder.FindAmongUsWithStorefront()` with a dedicated record:

```csharp
public record GameSearchResult
{
    public string? Path { get; init; }
    public Storefront? Storefront { get; init; }
    public bool DetectedButUnavailable { get; init; }
}
```

- `Path` — resolved Among Us folder, or `null`.
- `Storefront` — `Steam`, `Epic`, `MicrosoftStore`, or `null` when nothing found.
- `DetectedButUnavailable` — `true` when we have strong evidence of an install we
  can't use (locked folder, or manifest references the game but no folder resolves).
- `FindAmongUs()` keeps returning just `Path` for existing callers; it delegates to
  the new method and reads `.Path`.

### Epic detection (`GameFinder.FindAmongUsEpic`)

1. **Manifest parsing (primary):** read `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`
   (JSON). For each manifest that references Among Us (`DisplayName == "Among Us"`
   or `InstallLocation` ends in `Among Us`), verify `Among Us.exe` exists under the
   manifest's `InstallLocation` and return that folder. Wrap each file's
   deserialization in try/catch so one corrupted `.item` file cannot break the
   whole detection pass.
2. **Existing secondary checks (unchanged):** `GameUserSettings.ini`
   `DefaultInstallLocation=` parse, then the hardcoded fallback paths.
3. **DetectedButUnavailable:** if no folder resolves but a manifest **does**
   reference Among Us → return `GameSearchResult { Storefront = Epic,
   DetectedButUnavailable = true }`.

### Xbox / MS Store detection (`GameFinder.FindAmongUsXbox`)

1. **Dynamic drive scan:** iterate `DriveInfo.GetDrives()` filtered to
   `DriveType.Fixed && IsReady`, probing each root for a game folder named
   `Among Us` **or** `AmongUs`, then looking for `Among Us.exe` in these spots:
   - `<root>\Among Us\Among Us.exe`
   - `<root>\AmongUs\Among Us.exe`
   - `<root>\Among Us\Content\Among Us.exe`
   - `<root>\AmongUs\Content\Among Us.exe`
   If found → return that folder normally (MS Store install proceeds best-effort).
2. **WindowsApps scan (unchanged):** `Directory.GetDirectories(windowsApps, "Innersloth*")`.
   On access error, check presence via `IsAmongUsInstalledFromMsStore()`; if
   present → `GameSearchResult { Storefront = MicrosoftStore,
   DetectedButUnavailable = true }`.

### New: `GameFinder.IsAmongUsInstalledFromMsStore()`

Presence check that works without reading `WindowsApps`:
`Directory.GetDirectories("%LOCALAPPDATA%\Packages", "InnerSloth.LLC-*")` returns
any non-empty match. These `InnerSloth.LLC*` package folders exist for installed
Store apps under `%LOCALAPPDATA%\Packages` and are readable without admin.

### New: `Views/MsStoreAccessModal` (UserControl)

Dedicated popup content shown via the existing
`mainWindow.ModalOverlayControl.Show(title, content)` pattern.

- `Configure(Storefront storefront)` sets title and body text:
  - `MicrosoftStore`: title "Microsoft Store copy blocked"; explanation that the
    game lives in a protected `WindowsApps`/`XboxGames` folder Windows locks by
    default; answer: run the launcher as admin and/or apply the safe
    `takeown`/`icacls` grant on that one folder, then retry.
  - `Epic`: title "Epic install not found"; explanation that the Epic launcher
    shows Among Us installed but no readable folder was located; answer: verify
    the install location in Epic Games Launcher, then retry.
- A `Hyperlink` pinned at the bottom opening
  `https://github.com/FirethCrafts/Among-Launcher/blob/master/docs/adaptation-guide.md`
  via the default browser. In modern .NET, `Process.Start` requires
  `UseShellExecute = true` on the `ProcessStartInfo`, otherwise the URL is treated
  as a local executable and throws `Win32Exception`. Wrap in try/catch and log via
  `Debug.WriteLine` so a failure never crashes the UI thread.
- Single OK/Close button → `mainWindow.ModalOverlayControl.Hide()`.

## Data Flow

```
InstallButton_Click / Play check
  └─ GameFinder.FindAmongUsWithStorefront() → GameSearchResult
       ├─ Path != null           → normal flow (copy, BepInEx, launch)
       ├─ DetectedButUnavailable → show MsStoreAccessModal, abort
       └─ Path == null, not unavailable → existing "installation not found" message
```

## Error Handling

- Corrupted Epic `.item` manifest → try/catch per file, skip and continue.
- `WindowsApps` enumeration `UnauthorizedAccessException` → caught, converted to
  `DetectedButUnavailable` when presence confirmed.
- Hyperlink `Process.Start` failure → caught, no crash (log/ignore).

## Testing

- Build must produce 0 errors (pre-existing warnings tolerated).
- Smoke test: launch the app, confirm no startup regression.
- Detection unit checks via a temporary console harness or manual test with a
  mocked `XboxGames`/`WindowsApps` folder structure (create
  `%LOCALAPPDATA%\Packages\InnerSloth.LLC-Test` to exercise the presence path).
- Manual: verify modal renders with correct text per storefront and the guide
  link opens the GitHub page.
