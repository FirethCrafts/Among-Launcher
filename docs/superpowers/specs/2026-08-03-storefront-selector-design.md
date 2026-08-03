# Storefront Selector in Settings — Design

**Date:** 2026-08-03
**Status:** Approved (Approach A — Config storefront field + per-storefront search)

## Overview

Add a storefront selector (Steam / Epic / Microsoft Store / Auto) to the
"AMONG US PATH" card in Settings. Switching to a specific storefront re-searches
only that storefront and persists the choice so the main page's install and
launch flows follow it. Selecting "Auto" scans all storefronts: a single found
install auto-commits, multiple found installs open a picker modal, and nothing
found shows a "not found" status.

**Non-goals:**
- No change to detection logic itself (per-storefront searches already exist).
- No new NuGet packages.
- No change to launch-argument/BepInEx-bundle selection beyond what already
  flows from `_storefront`.

## Architecture

### Config: `LauncherConfig.Storefront`

```csharp
public Storefront? Storefront { get; set; }
```

`null` = the user has not chosen yet → main page keeps today's auto-detect
behavior. Existing config files load with `null`, so this is backward
compatible. The `Storefront` enum already lives in `GameDetection/Storefront.cs`.

### Detection: `GameFinder.FindAmongUsForStorefront`

```csharp
public static GameSearchResult FindAmongUsForStorefront(Storefront? storefront)
```

- `null` → `FindAmongUsWithStorefront()` (existing auto path).
- `Storefront.Steam` → wrap `FindAmongUsSteam()` in `GameSearchResult { Storefront = Steam }`.
- `Storefront.Epic` → `FindAmongUsEpic()`.
- `Storefront.MicrosoftStore` → `FindAmongUsXbox()`.

`AmongUsLocator` gets a matching convenience method so Views don't call the
static `GameFinder` directly.

### SettingsView: storefront combo

The "AMONG US PATH" card restructured:
- `GlassCombo` (reusing the existing `GlassCombo`/`GlassComboItem` styles) with
  4 items: Steam / Epic / Microsoft Store / Auto.
- `GamePathText` path display + Browse button unchanged.
- A new small status line (`StorefrontStatusText`) for Auto results.

**SelectionChanged storm guard** — an `_isInitializing` flag:

```csharp
private bool _isInitializing;

private void StorefrontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    if (_isInitializing || !IsLoaded) return;
    // interactive handling / Auto scan trigger
}
```

`SettingsView_Loaded` sets `_isInitializing = true`, restores the combo item
from `config.Storefront` (or Auto), clears the flag, then explicitly runs one
refresh/search for the restored value.

**Per-storefront selection:** combo → Steam/Epic/MS Store → call
`FindAmongUsForStorefront(sf)` → show path or "Not found" → save
`config.Storefront`. Selection stays as chosen even when not found.

**Auto behavior:**
1. Scan via `FindAmongUsWithStorefront()`.
2. Exactly one found → auto-commit: save `config.Storefront`, show path,
   status "Auto-detected: <storefront>". No modal.
3. Multiple found → open `StorefrontPickerModal` listing each found
   storefront + path; choosing one sets combo, saves, updates path.
4. None found → status "No Among Us installation found.", combo stays Auto.

### SettingsView: Browse interaction

Manual folder selection:
- Save `config.GamePath` (unchanged).
- Then determine whether the chosen folder matches a storefront by running
  each storefront's path check against it (via `FindAmongUsForStorefront` per
  storefront, comparing resolved path to the selected folder). If a match
  exists, set `config.Storefront` to it and update the combo (guarded). If no
  match, leave `config.Storefront` as-is.

### New: `Views/StorefrontPickerModal`

Content-only `UserControl` (title comes from `ModalOverlay.Show`):
- `ItemsControl` over the found `GameSearchResult` list; each row is a button
  showing the storefront name (bold) + path (muted, smaller).
- `StorefrontRow_Click` raises a `Selected` event with the chosen
  `GameSearchResult`.
- SettingsView handles the event: hide the modal, set combo + save.

Follows `MsStoreAccessModal` code-behind conventions.

### MainView integration

`CheckGameStatus()` and `InstallButton_Click` read `config.Storefront`:
- `null` → `FindAmongUsWithStorefront()` (today's behavior).
- set → `FindAmongUsForStorefront(sf)`.

`_storefront` is already assigned from the search result and drives
`GetLaunchArguments()` (`-EpicPortal`) and `BepInExInstaller` bundle choice —
so switching storefront in Settings changes install + launch automatically.

## Data Flow

```
SettingsView_Loaded
  → restore combo (guarded)
  → refresh search for restored storefront

Combo change (guarded)
  ├─ Steam/Epic/MS Store → search that storefront → path / "Not found" → save
  └─ Auto → scan all
       ├─ 1 found  → auto-commit + status
       ├─ N found  → StorefrontPickerModal → pick → save
       └─ 0 found  → "No Among Us installation found."

Browse
  → save GamePath
  → match folder to a storefront → save Storefront if matched

MainView CheckGameStatus / InstallButton_Click
  → config.Storefront null ? auto : FindAmongUsForStorefront(sf)
```

## Error Handling

- Corrupt/missing config → `Load()` returns defaults → Storefront null → auto.
- `_isInitializing` + `IsLoaded` guard prevents the Auto scan modal from
  opening during page load.
- No storefront found for a specific selection → path "Not found", selection kept.
- Browse folder matching no storefront → Storefront left unchanged.

## Testing

- Build must produce 0 errors (pre-existing warnings tolerated).
- Smoke test: launch app, open Settings, confirm no modal appears on load.
- Manual: switching combo re-searches; Auto with single install auto-commits;
  Auto with multiple installs opens picker; picker choice persists; Browse
  matching updates storefront; main page install uses saved storefront.
- Detection note: on the dev machine no Among Us install may exist — verify the
  "Not found"/"No Among Us installation found." paths render correctly.
