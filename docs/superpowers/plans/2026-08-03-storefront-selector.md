# Storefront Selector in Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a storefront selector (Steam/Epic/Microsoft Store/Auto) to the Settings "AMONG US PATH" card; the choice persists in config and drives the main page's install/launch.

**Architecture:** `LauncherConfig` gains a nullable `Storefront?` field; `GameFinder` gains `FindAmongUsForStorefront(Storefront?)`; `SettingsView` gets a `GlassCombo` (guarded against SelectionChanged storms) plus a new `StorefrontPickerModal` for the Auto multi-install case; `MainView` reads the saved storefront for install/launch.

**Tech Stack:** C# / .NET 10 WPF, no new NuGet packages.

## Global Constraints

- .NET 10 WPF app at `Among Launcher\Among Launcher.csproj`; namespace `AmongLauncher.GameDetection` for detection code, `AmongLauncher.Views` for views/modals, `AmongLauncher.Config` for config.
- No new NuGet packages.
- `Storefront` enum already exists: `Steam`, `Epic`, `MicrosoftStore` (in `GameDetection/Storefront.cs`). Reuse it; do not add new enum members.
- Existing `GlassCombo` (`x:Key="GlassCombo"`) and `GlassComboItem` (`x:Key="GlassComboItem"`) styles in `App.xaml` must be used for the combo.
- Modal pattern: `mainWindow.ModalOverlayControl.Show(string title, UIElement content)` where `mainWindow = Window.GetWindow(this) as MainWindow`.
- Modals shown on the UI thread; all `Dispatcher`-free (code-behind runs on UI thread).
- Build command: kill running `AmongLauncher` processes, then `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"` — must yield 0 errors (pre-existing warnings tolerated).
- Git: paths relative to repo root (`Among Launcher/...`), identity `dev@amonglauncher.com`. Do NOT push unless asked.
- There is no test project in this repo; verification is build + smoke test + manual UI checks.

---

### Task 1: Config field and storefront-scoped detection

**Files:**
- Modify: `Among Launcher/Config/LauncherConfig.cs`
- Modify: `Among Launcher/GameDetection/GameFinder.cs`
- Modify: `Among Launcher/GameDetection/AmongUsLocator.cs`

**Interfaces:**
- Consumes: `Storefront` enum (existing).
- Produces: `LauncherConfig.Storefront` (`Storefront?` property); `GameFinder.FindAmongUsForStorefront(Storefront? storefront)` returning `GameSearchResult`; `AmongUsLocator.FindAmongUsForStorefront(Storefront? storefront)` returning `GameSearchResult`.

- [ ] **Step 1: Add the config property**

In `Among Launcher/Config/LauncherConfig.cs`, add to the property block (after `GamePath`):

```csharp
public Storefront? Storefront { get; set; }
```

Add the using at the top:

```csharp
using AmongLauncher.GameDetection;
```

- [ ] **Step 2: Add `FindAmongUsForStorefront` to `GameFinder`**

In `Among Launcher/GameDetection/GameFinder.cs`, add a public method:

```csharp
public static GameSearchResult FindAmongUsForStorefront(Storefront? storefront)
{
    switch (storefront)
    {
        case Storefront.Steam:
            var steam = FindAmongUsSteam();
            return steam == null
                ? new GameSearchResult()
                : new GameSearchResult { Path = steam, Storefront = Storefront.Steam };

        case Storefront.Epic:
            return FindAmongUsEpic();

        case Storefront.MicrosoftStore:
            return FindAmongUsXbox();

        default:
            return FindAmongUsWithStorefront();
    }
}
```

- [ ] **Step 3: Add `FindAmongUsForStorefront` to `AmongUsLocator`**

In `Among Launcher/GameDetection/AmongUsLocator.cs`:

```csharp
public GameSearchResult FindAmongUsForStorefront(Storefront? storefront)
{
    return GameFinder.FindAmongUsForStorefront(storefront);
}
```

- [ ] **Step 4: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Config/LauncherConfig.cs" "Among Launcher/GameDetection/GameFinder.cs" "Among Launcher/GameDetection/AmongUsLocator.cs"
git commit -m "feat: storefront field in config and storefront-scoped detection"
```

---

### Task 2: `StorefrontPickerModal` UserControl

**Files:**
- Create: `Among Launcher/Views/StorefrontPickerModal.xaml`
- Create: `Among Launcher/Views/StorefrontPickerModal.xaml.cs`

**Interfaces:**
- Consumes: `GameSearchResult` (from Task 1's feature work, already in `GameDetection/GameSearchResult.cs`); `TextBody` brush and `GlassCard` style from `App.xaml`.
- Produces: `StorefrontPickerModal.SetResults(IEnumerable<GameSearchResult>)`; event `Selected` of type `EventHandler<GameSearchResult>`.

- [ ] **Step 1: Write the XAML**

Create `Among Launcher/Views/StorefrontPickerModal.xaml`:

```xml
<UserControl x:Class="AmongLauncher.Views.StorefrontPickerModal"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             mc:Ignorable="d"
             d:DesignHeight="300" d:DesignWidth="440">
    <StackPanel>
        <TextBlock Text="Multiple Among Us installations were found on your system. Choose which one to use:"
                   FontSize="13" Foreground="{StaticResource TextBody}" TextWrapping="Wrap" Margin="0,0,0,16"/>
        <ItemsControl x:Name="StorefrontList">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Button Click="StorefrontRow_Click" Margin="0,4" Padding="12,8"
                            Style="{StaticResource SecondaryButton}"
                            HorizontalContentAlignment="Stretch">
                        <StackPanel>
                            <TextBlock FontWeight="Bold" FontSize="13"
                                       Foreground="{StaticResource TextBody}">
                                <Run Text="{Binding Storefront}"/>
                            </TextBlock>
                            <TextBlock Text="{Binding Path}" Opacity="0.6" FontSize="11"
                                       TextTrimming="CharacterEllipsis"
                                       Foreground="{StaticResource TextBody}"/>
                        </StackPanel>
                    </Button>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </StackPanel>
</UserControl>
```

- [ ] **Step 2: Write the code-behind**

Create `Among Launcher/Views/StorefrontPickerModal.xaml.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class StorefrontPickerModal : UserControl
{
    public event EventHandler<GameSearchResult>? Selected;

    public StorefrontPickerModal()
    {
        InitializeComponent();
    }

    public void SetResults(IEnumerable<GameSearchResult> results)
    {
        StorefrontList.ItemsSource = results;
    }

    private void StorefrontRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: GameSearchResult result })
        {
            Selected?.Invoke(this, result);
        }
    }
}
```

- [ ] **Step 3: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Views/StorefrontPickerModal.xaml" "Among Launcher/Views/StorefrontPickerModal.xaml.cs"
git commit -m "feat: storefront picker modal"
```

---

### Task 3: SettingsView storefront combo + Auto scan

**Files:**
- Modify: `Among Launcher/Views/SettingsView.xaml`
- Modify: `Among Launcher/Views/SettingsView.xaml.cs`

**Interfaces:**
- Consumes: `GlassCombo`/`GlassComboItem` styles; `StorefrontPickerModal` (Task 2); `LauncherConfig.Storefront` and `AmongUsLocator.FindAmongUsForStorefront` (Task 1); `ModalOverlay` pattern.
- Produces: A combo (`x:Name="StorefrontCombo"`) and status text (`x:Name="StorefrontStatusText"`) inside the "AMONG US PATH" card; `_isInitializing` guard; the Auto scan + picker flow.

- [ ] **Step 1: Update the SettingsView XAML**

In `Among Launcher/Views/SettingsView.xaml`, replace the "AMONG US PATH" card's `StackPanel` (lines 26-31) with:

```xml
<StackPanel Grid.Column="0">
    <TextBlock Text="AMONG US PATH" Style="{StaticResource CardHeader}"/>
    <ComboBox x:Name="StorefrontCombo" Style="{StaticResource GlassCombo}"
              SelectionChanged="StorefrontCombo_SelectionChanged"
              Margin="0,4,0,8" Width="200" HorizontalAlignment="Left"/>
    <TextBlock x:Name="GamePathText" Text="Auto-detecting..."
               TextTrimming="CharacterEllipsis" FontSize="13"
               Foreground="{StaticResource TextMuted}"/>
    <TextBlock x:Name="StorefrontStatusText" Text="" FontSize="11"
               Foreground="{StaticResource TextMuted}" Margin="0,4,0,0"/>
</StackPanel>
```

- [ ] **Step 2: Rewrite the SettingsView code-behind**

Replace `Among Launcher/Views/SettingsView.xaml.cs` entirely:

```csharp
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using AmongLauncher.GameDetection;

namespace AmongLauncher.Views;

public partial class SettingsView
{
    private bool _isInitializing;
    private readonly AmongUsLocator _locator = new();

    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, RoutedEventArgs e)
    {
        var config = Config.LauncherConfig.Load();
        ServerUrlTextBox.Text = config.ServerUrl;

        _isInitializing = true;
        if (StorefrontCombo.Items.Count == 0)
        {
            StorefrontCombo.Items.Add("Steam");
            StorefrontCombo.Items.Add("Epic");
            StorefrontCombo.Items.Add("Microsoft Store");
            StorefrontCombo.Items.Add("Auto");
        }
        StorefrontCombo.SelectedItem = config.Storefront switch
        {
            Storefront.Steam => "Steam",
            Storefront.Epic => "Epic",
            Storefront.MicrosoftStore => "Microsoft Store",
            _ => "Auto"
        };
        _isInitializing = false;

        RefreshStorefrontSearch(config.Storefront);
    }

    private void StorefrontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || !IsLoaded) return;

        var storefront = SelectedStorefront();
        RefreshStorefrontSearch(storefront);
        SaveStorefront(storefront);
    }

    private Storefront? SelectedStorefront()
    {
        return StorefrontCombo.SelectedItem as string switch
        {
            "Steam" => Storefront.Steam,
            "Epic" => Storefront.Epic,
            "Microsoft Store" => Storefront.MicrosoftStore,
            _ => null
        };
    }

    private void RefreshStorefrontSearch(Storefront? storefront)
    {
        var result = _locator.FindAmongUsForStorefront(storefront);

        if (result.Path != null)
        {
            GamePathText.Text = result.Path;
            StorefrontStatusText.Text = "";
            return;
        }

        GamePathText.Text = "Not found";

        if (storefront == null)
        {
            var all = _locator.FindAmongUsWithStorefront();
            if (all.DetectedButUnavailable)
            {
                StorefrontStatusText.Text =
                    "An install was detected but is inaccessible. See the guide in the main install flow.";
            }
            else
            {
                StorefrontStatusText.Text = "No Among Us installation found.";
            }
        }
        else
        {
            StorefrontStatusText.Text = "";
        }
    }

    private void SaveStorefront(Storefront? storefront)
    {
        var config = Config.LauncherConfig.Load();
        config.Storefront = storefront;
        config.Save();
    }

    private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Among Us Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var selectedFolder = dialog.FolderName;
        GamePathText.Text = selectedFolder;

        var config = Config.LauncherConfig.Load();
        config.GamePath = selectedFolder;

        var matched = MatchStorefrontToFolder(selectedFolder);
        if (matched.HasValue)
        {
            config.Storefront = matched.Value;
            _isInitializing = true;
            StorefrontCombo.SelectedItem = matched.Value switch
            {
                Storefront.Steam => "Steam",
                Storefront.Epic => "Epic",
                Storefront.MicrosoftStore => "Microsoft Store",
                _ => "Auto"
            };
            _isInitializing = false;
        }

        config.Save();
    }

    private Storefront? MatchStorefrontToFolder(string folder)
    {
        foreach (var storefront in new[] { Storefront.Steam, Storefront.Epic, Storefront.MicrosoftStore })
        {
            var result = _locator.FindAmongUsForStorefront(storefront);
            if (result.Path != null &&
                string.Equals(result.Path.TrimEnd('\\'), folder.TrimEnd('\\'), System.StringComparison.OrdinalIgnoreCase))
            {
                return storefront;
            }
        }

        return null;
    }

    private void ResetInstall_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This will delete the modded Among Us installation and copy it again from your game library.\n\nContinue?",
            "Reset Installation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var moddedPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmongLauncher", "ModdedAmongUs");

        if (System.IO.Directory.Exists(moddedPath))
        {
            System.IO.Directory.Delete(moddedPath, true);
        }

        MessageBox.Show("Modded installation deleted. Click 'Install BepInEx' on the main page to set it up again.",
            "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
```

Note: `RefreshStorefrontSearch` currently treats the "Auto" case with a single-found install by simply showing the path — the auto-commit + picker logic is added in Task 4 (the multi-install picker needs the modal, which Task 4 wires). This task keeps the SearchResult display working so the view is fully functional for the per-storefront and Auto paths.

- [ ] **Step 3: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

- [ ] **Step 4: Smoke test**

Run:
```powershell
Start-Process "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\bin\Debug\net10.0-windows\AmongLauncher.exe"
Start-Sleep -Seconds 3
Get-Process AmongLauncher -ErrorAction SilentlyContinue | Select-Object Id, HasExited
```
Expected: a live process. Kill it afterwards. Also verify opening Settings does **not** pop any modal (the `_isInitializing` guard).

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Views/SettingsView.xaml" "Among Launcher/Views/SettingsView.xaml.cs"
git commit -m "feat: storefront combo with auto scan in settings"
```

---

### Task 4: Auto multi-install picker flow

**Files:**
- Modify: `Among Launcher/Views/SettingsView.xaml.cs`

**Interfaces:**
- Consumes: `StorefrontPickerModal.Selected` (Task 2); `RefreshStorefrontSearch` + `SaveStorefront` + `SelectedStorefront` (Task 3).
- Produces: Auto selection with multiple found installs opens the picker; picking commits the storefront.

- [ ] **Step 1: Add the picker flow to `RefreshStorefrontSearch`**

In `Among Launcher/Views/SettingsView.xaml.cs`, modify `RefreshStorefrontSearch` so the Auto (null) case scans for all installs and opens the picker when more than one distinct install is found:

```csharp
private void RefreshStorefrontSearch(Storefront? storefront)
{
    var result = _locator.FindAmongUsForStorefront(storefront);

    if (result.Path != null)
    {
        GamePathText.Text = result.Path;
        StorefrontStatusText.Text = "";
        return;
    }

    GamePathText.Text = "Not found";

    if (storefront == null)
    {
        var all = _locator.FindAmongUsWithStorefront();

        if (all.DetectedButUnavailable)
        {
            StorefrontStatusText.Text =
                "An install was detected but is inaccessible. See the guide in the main install flow.";
            return;
        }

        var found = new List<GameSearchResult>();
        foreach (var sf in new[] { Storefront.Steam, Storefront.Epic, Storefront.MicrosoftStore })
        {
            var candidate = _locator.FindAmongUsForStorefront(sf);
            if (candidate.Path != null) found.Add(candidate);
        }

        if (found.Count == 0)
        {
            StorefrontStatusText.Text = "No Among Us installation found.";
            return;
        }

        if (found.Count == 1)
        {
            var only = found[0];
            GamePathText.Text = only.Path;
            SaveStorefront(only.Storefront);
            StorefrontStatusText.Text = $"Auto-detected: {only.Storefront}";
            SyncComboToStorefront(only.Storefront);
            return;
        }

        OpenPicker(found);
    }
    else
    {
        StorefrontStatusText.Text = "";
    }
}
```

- [ ] **Step 2: Add the picker + sync helpers**

Add to `SettingsView.xaml.cs`:

```csharp
private void OpenPicker(List<GameSearchResult> found)
{
    var mainWindow = Window.GetWindow(this) as MainWindow;
    if (mainWindow == null) return;

    var picker = new StorefrontPickerModal();
    picker.SetResults(found);
    picker.Selected += (_, chosen) =>
    {
        mainWindow.ModalOverlayControl.Hide();
        if (chosen.Storefront.HasValue)
        {
            SaveStorefront(chosen.Storefront.Value);
            SyncComboToStorefront(chosen.Storefront.Value);
        }
        GamePathText.Text = chosen.Path;
        StorefrontStatusText.Text = "";
    };

    mainWindow.ModalOverlayControl.Show("Choose Among Us Installation", picker);
}

private void SyncComboToStorefront(Storefront storefront)
{
    _isInitializing = true;
    StorefrontCombo.SelectedItem = storefront switch
    {
        Storefront.Steam => "Steam",
        Storefront.Epic => "Epic",
        Storefront.MicrosoftStore => "Microsoft Store",
        _ => "Auto"
    };
    _isInitializing = false;
}
```

Add `using System.Collections.Generic;` to the top of the file.

- [ ] **Step 3: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

- [ ] **Step 4: Smoke test**

Same as Task 3 Step 4 — launch, wait 3s, confirm alive, kill.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Views/SettingsView.xaml.cs"
git commit -m "feat: picker modal when auto finds multiple storefronts"
```

---

### Task 5: MainView uses the saved storefront

**Files:**
- Modify: `Among Launcher/Views/MainView.xaml.cs`

**Interfaces:**
- Consumes: `LauncherConfig.Storefront` and `AmongUsLocator.FindAmongUsForStorefront` (Task 1).
- Produces: Main page search respects the saved storefront.

- [ ] **Step 1: Update `CheckGameStatus`**

In `Among Launcher/Views/MainView.xaml.cs`, in `CheckGameStatus()`, replace lines 49-50:

```csharp
var locator = new AmongUsLocator();
var gamePath = locator.FindAmongUs();
```

with:

```csharp
var locator = new AmongUsLocator();
var storefront = Config.LauncherConfig.Load().Storefront;
var gamePath = locator.FindAmongUsForStorefront(storefront).Path;
```

- [ ] **Step 2: Update `InstallButton_Click`**

In `InstallButton_Click`, replace lines 89-90:

```csharp
var locator = new AmongUsLocator();
var result = locator.FindAmongUsWithStorefront();
```

with:

```csharp
var locator = new AmongUsLocator();
var storefront = Config.LauncherConfig.Load().Storefront;
var result = locator.FindAmongUsForStorefront(storefront);
```

- [ ] **Step 3: Verify build passes**

Run: `dotnet build "C:\Users\meowfire\RiderProjects\Among Launcher\Among Launcher\Among Launcher.csproj"`
Expected: 0 errors.

- [ ] **Step 4: Smoke test**

Launch, wait 3s, confirm alive, kill.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Views/MainView.xaml.cs"
git commit -m "feat: main page respects saved storefront for install and status"
```
