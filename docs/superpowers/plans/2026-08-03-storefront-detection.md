# Storefront Detection & Unavailable-Install Popup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Improve Epic and Xbox/MS Store Among Us detection, and show a dedicated popup (with a guide link) when an install is detected but unusable.

**Architecture:** Replace the `(string? Path, Storefront? Storefront)` tuple with a `GameSearchResult` record. Enhance `GameFinder.FindAmongUsEpic` to parse Epic manifests, and `GameFinder.FindAmongUsXbox` to scan all fixed drives plus report locked `WindowsApps` installs. Add a `MsStoreAccessModal` UserControl shown via the existing `ModalOverlay` when detection reports `DetectedButUnavailable`.

**Tech Stack:** C# / .NET 10 WPF, no new NuGet packages.

## Global Constraints

- .NET 10 WPF app at `Among Launcher\Among Launcher.csproj`; namespace `AmongLauncher.GameDetection` for detection code, `AmongLauncher.Views` for the modal.
- No new NuGet packages.
- `FindAmongUs()` must keep returning just `string?` for existing callers.
- `GameSearchResult` uses `init`-only properties exactly as specified in the spec.
- Guide URL: `https://github.com/FirethCrafts/Among-Launcher/blob/master/docs/adaptation-guide.md`; opening it via `Process.Start` **must** set `UseShellExecute = true` and be wrapped in try/catch (`Debug.WriteLine` on failure).
- Every `.item` manifest parse is wrapped in its own try/catch.
- Build command: kill running `AmongLauncher` processes, then `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"` — must yield 0 errors (pre-existing warnings tolerated).
- Git: paths are relative to repo root (`Among Launcher/...`), identity `dev@amonglauncher.com`. Do NOT push unless asked.

---

### Task 1: `GameSearchResult` record and GameFinder refactor

**Files:**
- Create: `Among Launcher/GameDetection/GameSearchResult.cs`
- Modify: `Among Launcher/GameDetection/GameFinder.cs`
- Modify: `Among Launcher/GameDetection/AmongUsLocator.cs`
- Modify: `Among Launcher/Views/MainView.xaml.cs:87-98`

**Interfaces:**
- Consumes: `Storefront` enum (`Steam`, `Epic`, `MicrosoftStore`) already in `GameDetection/Storefront.cs`.
- Produces: `GameSearchResult` record with `string? Path`, `Storefront? Storefront`, `bool DetectedButUnavailable` (all `init`); `GameFinder.FindAmongUsWithStorefront()` returning `GameSearchResult`; `AmongUsLocator.FindAmongUsWithStorefront()` returning `GameSearchResult`.

- [ ] **Step 1: Create the `GameSearchResult` record**

Create `Among Launcher/GameDetection/GameSearchResult.cs`:

```csharp
namespace AmongLauncher.GameDetection;

public record GameSearchResult
{
    public string? Path { get; init; }
    public Storefront? Storefront { get; init; }
    public bool DetectedButUnavailable { get; init; }
}
```

- [ ] **Step 2: Update `GameFinder` to return the record**

In `Among Launcher/GameDetection/GameFinder.cs`, replace the method bodies (keep the private Steam/Epic/Xbox helpers untouched for now — Task 2/3 modify them):

```csharp
public static string? FindAmongUs() => FindAmongUsWithStorefront().Path;

public static GameSearchResult FindAmongUsWithStorefront()
{
    var steam = FindAmongUsSteam();
    if (steam != null) return new GameSearchResult { Path = steam, Storefront = Storefront.Steam };

    var epic = FindAmongUsEpic();
    if (epic.Path != null) return epic;

    var xbox = FindAmongUsXbox();
    if (xbox.Path != null) return xbox;

    return epic.DetectedButUnavailable
        ? epic
        : xbox.DetectedButUnavailable
            ? xbox
            : new GameSearchResult();
}
```

Change the private helpers' return types to `GameSearchResult` so the code compiles:

```csharp
private static GameSearchResult FindAmongUsEpic()
{
    // existing body; on success: return new GameSearchResult { Path = <folder>, Storefront = Storefront.Epic };
    // on failure: return new GameSearchResult();
}

private static GameSearchResult FindAmongUsXbox()
{
    // existing body; on success: return new GameSearchResult { Path = <folder>, Storefront = Storefront.MicrosoftStore };
    // on failure: return new GameSearchResult();
}
```

Keep `FindAmongUsSteam()` returning `string?` as-is (Steam returns `(path, Steam)` inline in `FindAmongUsWithStorefront`).

- [ ] **Step 3: Update `AmongUsLocator`**

In `Among Launcher/GameDetection/AmongUsLocator.cs`:

```csharp
public GameSearchResult FindAmongUsWithStorefront()
{
    return GameFinder.FindAmongUsWithStorefront();
}
```

- [ ] **Step 4: Update `MainView` install handler to destructure the record**

In `Among Launcher/Views/MainView.xaml.cs` `InstallButton_Click`, replace lines 89-98:

```csharp
var locator = new AmongUsLocator();
var result = locator.FindAmongUsWithStorefront();

if (result.DetectedButUnavailable)
{
    ShowMsStoreAccessModal(result.Storefront);
    return;
}

if (result.Path == null)
{
    MessageBox.Show("Among Us installation not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    return;
}

var sourcePath = result.Path;
var storefront = result.Storefront;

_storefront = storefront ?? Storefront.Steam;
```

Add the placeholder modal method (Task 5 replaces the body with the real modal):

```csharp
private void ShowMsStoreAccessModal(Storefront? storefront)
{
    var mainWindow = Window.GetWindow(this) as MainWindow;
    if (mainWindow == null) return;
    var modal = new MsStoreAccessModal();
    modal.Configure(storefront);
    mainWindow.ModalOverlayControl.Show(
        storefront == Storefront.Epic ? "Epic install not found" : "Microsoft Store copy blocked",
        modal);
}
```

`MsStoreAccessModal` does not exist yet, so the build is expected to fail at this step. That's OK — Task 5 creates it.

- [ ] **Step 5: Verify the refactor compiles except for the missing modal**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"` (after killing running processes)
Expected: errors only for `MsStoreAccessModal` / `Configure` (namespace `AmongLauncher.Views`); no other errors.

- [ ] **Step 6: Commit**

```bash
git add "Among Launcher/GameDetection/GameSearchResult.cs" "Among Launcher/GameDetection/GameFinder.cs" "Among Launcher/GameDetection/AmongUsLocator.cs" "Among Launcher/Views/MainView.xaml.cs"
git commit -m "refactor: GameSearchResult record replaces storefront tuple"
```

---

### Task 2: Epic manifest detection

**Files:**
- Modify: `Among Launcher/GameDetection/GameFinder.cs` (`FindAmongUsEpic`)

**Interfaces:**
- Consumes: `GameSearchResult` (Task 1), `Storefront.Epic`.
- Produces: `FindAmongUsEpic()` returning `GameSearchResult` that resolves the install folder from Epic manifests, or `DetectedButUnavailable = true` when a manifest references Among Us but no folder resolves.

- [ ] **Step 1: Write the Epic manifest parsing**

Replace `FindAmongUsEpic()` in `Among Launcher/GameDetection/GameFinder.cs` with:

```csharp
private static GameSearchResult FindAmongUsEpic()
{
    try
    {
        var manifestsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (Directory.Exists(manifestsDir))
        {
            var manifestFound = false;
            foreach (var manifestFile in Directory.GetFiles(manifestsDir, "*.item"))
            {
                try
                {
                    var json = File.ReadAllText(manifestFile);
                    var item = System.Text.Json.JsonDocument.Parse(json).RootElement;

                    var displayName = GetString(item, "DisplayName");
                    var installLocation = GetString(item, "InstallLocation");

                    var matchesName = displayName != null &&
                        displayName.Equals("Among Us", StringComparison.OrdinalIgnoreCase);
                    var matchesPath = installLocation != null &&
                        installLocation.EndsWith("Among Us", StringComparison.OrdinalIgnoreCase);

                    if (matchesName || matchesPath)
                    {
                        manifestFound = true;
                        if (installLocation != null)
                        {
                            var direct = Path.Combine(installLocation, AmongUsExe);
                            if (File.Exists(direct))
                            {
                                return new GameSearchResult
                                {
                                    Path = installLocation,
                                    Storefront = Storefront.Epic
                                };
                            }

                            var nested = Path.Combine(installLocation, AmongUsFolder, AmongUsExe);
                            if (File.Exists(nested))
                            {
                                return new GameSearchResult
                                {
                                    Path = Path.Combine(installLocation, AmongUsFolder),
                                    Storefront = Storefront.Epic
                                };
                            }
                        }
                    }
                }
                catch
                {
                    // Corrupted .item file — skip and continue.
                }
            }

            if (manifestFound)
            {
                return new GameSearchResult { Storefront = Storefront.Epic, DetectedButUnavailable = true };
            }
        }
    }
    catch
    {
        // Manifests dir unreadable — fall through to secondary checks.
    }

    // Existing secondary checks: GameUserSettings.ini DefaultInstallLocation, then fallback paths.
    try
    {
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Epic", "EpicGamesLauncher", "Saved", "Config", "Windows", "GameUserSettings.ini");

        if (File.Exists(configPath))
        {
            var lines = File.ReadAllLines(configPath);
            foreach (var line in lines)
            {
                if (!line.StartsWith("DefaultInstallLocation=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var installDir = line.Substring("DefaultInstallLocation=".Length).Trim().Trim('"');
                if (string.IsNullOrEmpty(installDir)) continue;

                var gamePath = Path.Combine(installDir, AmongUsFolder, AmongUsExe);
                if (File.Exists(gamePath))
                {
                    return new GameSearchResult
                    {
                        Path = Path.Combine(installDir, AmongUsFolder),
                        Storefront = Storefront.Epic
                    };
                }
            }
        }
    }
    catch { }

    var epicFallback = new[]
    {
        @"C:\Program Files\Epic Games\Among Us",
        @"D:\Epic Games\Among Us",
        @"E:\Epic Games\Among Us"
    };

    foreach (var path in epicFallback)
    {
        if (File.Exists(Path.Combine(path, AmongUsExe)))
        {
            return new GameSearchResult { Path = path, Storefront = Storefront.Epic };
        }
    }

    return new GameSearchResult();
}
```

Note: the original code resolved the folder as `installDir` directly (the config `DefaultInstallLocation` pointed at `...\Among Us`). The manifest `InstallLocation` points at the parent, so `Path.Combine(installLocation, AmongUsFolder)` is used there. The fallback paths already point at the game folder itself.

Add the private helper at the bottom of the class:

```csharp
private static string? GetString(System.Text.Json.JsonElement element, string propertyName)
{
    if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
        return value.GetString();
    return null;
}
```

- [ ] **Step 2: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors (task can be verified independently of Task 3).

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/GameDetection/GameFinder.cs"
git commit -m "feat: detect Epic Among Us via launcher manifests"
```

---

### Task 3: Xbox / MS Store detection

**Files:**
- Modify: `Among Launcher/GameDetection/GameFinder.cs` (`FindAmongUsXbox`, new `IsAmongUsInstalledFromMsStore`)

**Interfaces:**
- Consumes: `GameSearchResult` (Task 1), `Storefront.MicrosoftStore`.
- Produces: `FindAmongUsXbox()` returning `GameSearchResult`; `GameFinder.IsAmongUsInstalledFromMsStore()` returning `bool`.

- [ ] **Step 1: Write the dynamic drive scan + locked-folder detection**

Replace `FindAmongUsXbox()` in `Among Launcher/GameDetection/GameFinder.cs` with:

```csharp
private static GameSearchResult FindAmongUsXbox()
{
    foreach (var drive in DriveInfo.GetDrives())
    {
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

        var root = drive.RootDirectory.FullName;

        foreach (var gameFolder in new[] { "Among Us", "AmongUs" })
        {
            var candidates = new[]
            {
                Path.Combine(root, gameFolder, AmongUsExe),
                Path.Combine(root, gameFolder, "Content", AmongUsExe)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return new GameSearchResult
                    {
                        Path = Path.GetDirectoryName(candidate),
                        Storefront = Storefront.MicrosoftStore
                    };
                }
            }
        }
    }

    try
    {
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");

        if (Directory.Exists(windowsApps))
        {
            var amongDirs = Directory.GetDirectories(windowsApps, "Innersloth*");
            foreach (var dir in amongDirs.OrderByDescending(d => new DirectoryInfo(d).LastWriteTime))
            {
                var gamePath = Path.Combine(dir, AmongUsExe);
                if (File.Exists(gamePath))
                {
                    return new GameSearchResult
                    {
                        Path = dir,
                        Storefront = Storefront.MicrosoftStore
                    };
                }
            }
        }
    }
    catch (UnauthorizedAccessException)
    {
        if (IsAmongUsInstalledFromMsStore())
        {
            return new GameSearchResult { Storefront = Storefront.MicrosoftStore, DetectedButUnavailable = true };
        }
    }
    catch { }

    return new GameSearchResult();
}
```

Add the presence check method:

```csharp
public static bool IsAmongUsInstalledFromMsStore()
{
    try
    {
        var packagesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages");

        return Directory.Exists(packagesDir) &&
            Directory.GetDirectories(packagesDir, "InnerSloth.LLC-*").Length > 0;
    }
    catch
    {
        return false;
    }
}
```

- [ ] **Step 2: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

- [ ] **Step 3: Verify presence detection manually**

Run:
```powershell
New-Item -ItemType Directory -Path "$env:LOCALAPPDATA\Packages\InnerSloth.LLC-Test" -Force | Out-Null
```
Then launch the app (`Start-Process ...AmongLauncher.exe`), wait 3s, confirm it stays alive, kill it. Then remove the test folder:
```powershell
Remove-Item "$env:LOCALAPPDATA\Packages\InnerSloth.LLC-Test" -Recurse -Force
```

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/GameDetection/GameFinder.cs"
git commit -m "feat: detect Xbox/MS Store Among Us across all fixed drives"
```

---

### Task 4: `MsStoreAccessModal` UserControl

**Files:**
- Create: `Among Launcher/Views/MsStoreAccessModal.xaml`
- Create: `Among Launcher/Views/MsStoreAccessModal.xaml.cs`

**Interfaces:**
- Consumes: `Storefront` enum; existing `TextBody` brush and `StopButton`/`SecondaryButton` styles from `App.xaml`.
- Produces: `MsStoreAccessModal.Configure(Storefront? storefront)`; auto-wired into `App.xaml` as a `UserControl` via its `<ResourceDictionary>` merge (the project builds all `.xaml` in the project by default).

- [ ] **Step 1: Write the XAML**

Create `Among Launcher/Views/MsStoreAccessModal.xaml`:

```xml
<UserControl x:Class="AmongLauncher.Views.MsStoreAccessModal"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             mc:Ignorable="d"
             d:DesignHeight="320" d:DesignWidth="460">
    <StackPanel>
        <TextBlock x:Name="ExplanationText" FontSize="14"
                   Foreground="{StaticResource TextBody}" TextWrapping="Wrap" Margin="0,0,0,16"/>
        <TextBlock x:Name="AnswerText" FontSize="14"
                   Foreground="{StaticResource TextBody}" TextWrapping="Wrap" Margin="0,0,0,20"/>
        <TextBlock FontSize="13" Foreground="{StaticResource TextMuted}" Margin="0,0,0,24">
            <Hyperlink x:Name="GuideLink" NavigateUri="https://github.com/FirethCrafts/Among-Launcher/blob/master/docs/adaptation-guide.md">
                Guide: How to fix this
            </Hyperlink>
        </TextBlock>
        <Button x:Name="OkButton" Content="OK" Click="OkButton_Click"
                Style="{StaticResource StopButton}"
                Height="36" Padding="24,8" HorizontalAlignment="Right"/>
    </StackPanel>
</UserControl>
```

- [ ] **Step 2: Write the code-behind**

Create `Among Launcher/Views/MsStoreAccessModal.xaml.cs`:

```csharp
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class MsStoreAccessModal : UserControl
{
    public MsStoreAccessModal()
    {
        InitializeComponent();

        GuideLink.RequestNavigate += GuideLink_RequestNavigate;
    }

    public void Configure(Storefront? storefront)
    {
        if (storefront == Storefront.Epic)
        {
            ExplanationText.Text =
                "The Epic Games Launcher shows Among Us as installed, but the launcher couldn't find a readable " +
                "game folder. This can happen when the game is installed to an unusual location or the launcher " +
                "hasn't finished installing it.";
            AnswerText.Text =
                "Open Epic Games Launcher, confirm the Among Us install location, then click Install again.";
        }
        else
        {
            ExplanationText.Text =
                "Among Us from the Microsoft Store lives in a protected Windows folder that's locked by default, " +
                "so the launcher can't copy the game files to make a modded install.";
            AnswerText.Text =
                "Run the launcher as administrator, or grant read access to that one folder with takeown and " +
                "icacls (see the guide), then click Install again. Mods are not guaranteed to work on the " +
                "Microsoft Store version.";
        }
    }

    private void GuideLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open guide URL: {ex.Message}");
        }

        e.Handled = true;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.ModalOverlayControl.Hide();
    }
}
```

- [ ] **Step 3: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors — this also resolves Task 1's missing-modal errors.

- [ ] **Step 4: Smoke test**

Run:
```powershell
Start-Process "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\bin\Debug\net10.0-windows\AmongLauncher.exe"
Start-Sleep -Seconds 3
Get-Process AmongLauncher -ErrorAction SilentlyContinue | Select-Object Id, HasExited
```
Expected: a live process. Kill it afterwards.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Views/MsStoreAccessModal.xaml" "Among Launcher/Views/MsStoreAccessModal.xaml.cs"
git commit -m "feat: MS Store / Epic unavailable-install popup with guide link"
```

---

### Task 5: Wire the modal into the Play flow

**Files:**
- Modify: `Among Launcher/Views/MainView.xaml.cs` (`PlayButton_Click`, `LaunchGame`)

**Interfaces:**
- Consumes: `MsStoreAccessModal` (Task 4), `ShowMsStoreAccessModal` (Task 1), `GameFinder.FindAmongUsWithStorefront()`.

- [ ] **Step 1: Add a shared unavailable-check helper**

In `Among Launcher/Views/MainView.xaml.cs`, add:

```csharp
private bool TryShowUnavailableInstall()
{
    if (!string.IsNullOrEmpty(_moddedPath)) return false;

    var result = new AmongUsLocator().FindAmongUsWithStorefront();
    if (!result.DetectedButUnavailable) return false;

    ShowMsStoreAccessModal(result.Storefront);
    return true;
}
```

- [ ] **Step 2: Update `PlayButton_Click`**

In `PlayButton_Click` (around line 175), replace:

```csharp
if (string.IsNullOrEmpty(_moddedPath)) return;
```

with:

```csharp
if (string.IsNullOrEmpty(_moddedPath))
{
    if (TryShowUnavailableInstall()) return;
    return;
}
```

- [ ] **Step 3: Update `LaunchGame`**

In `LaunchGame` (around line 202), replace:

```csharp
if (string.IsNullOrEmpty(_moddedPath)) return;
```

with:

```csharp
if (string.IsNullOrEmpty(_moddedPath))
{
    if (TryShowUnavailableInstall()) return;
    return;
}
```

- [ ] **Step 4: Verify build + smoke test**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

Then smoke test (launch, wait 3s, confirm alive, kill).

- [ ] **Step 5: Manual modal verification**

With the app running, temporarily create a presence signal to force `DetectedButUnavailable` (or trigger the modal by clicking Install on a machine with a locked Store install). If using the fake presence folder:

```powershell
New-Item -ItemType Directory -Path "$env:LOCALAPPDATA\Packages\InnerSloth.LLC-Test" -Force | Out-Null
```
Confirm the modal shows the Microsoft Store explanation and that the guide link opens the GitHub page in the browser. Then clean up:
```powershell
Remove-Item "$env:LOCALAPPDATA\Packages\InnerSloth.LLC-Test" -Recurse -Force
```

- [ ] **Step 6: Commit**

```bash
git add "Among Launcher/Views/MainView.xaml.cs"
git commit -m "feat: show unavailable-install popup from Play flow"
```
