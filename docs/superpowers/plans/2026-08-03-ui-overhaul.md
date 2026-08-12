# AmongLauncher Glass UI Overhaul Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the AmongLauncher WPF interface from flat matte to a layered glass/frosted design with a shared animation system, per the approved spec `docs/superpowers/specs/2026-08-03-ui-overhaul-design.md`.

**Architecture:** Presentation-only overhaul. New glass token palette + reusable `AmbientBackground` layer in `App.xaml`/`MainWindow`, shared `Storyboard` resources drive hover glows, entrances, and ambient motion. Every view (Main, Welcome, Settings, modals) swaps matte styles for glass styles. No launcher logic, IPC, or server behavior changes.

**Tech Stack:** WPF (.NET 10, net10.0-windows), pure XAML + minimal C#. No new NuGet dependencies.

## Global Constraints

- Target framework stays `net10.0-windows`. No new NuGet packages.
- No changes to launcher logic: `MainWindow.xaml.cs`, `MainView.xaml.cs`, IPC, installers, config, or server code — only `.xaml` files and, where necessary, one new `AmbientBackground` control and a reduce-motion static in `App.xaml.cs`.
- Surface translucency is expressed as 8-digit ARGB hex. Surfaces are translucent over the **ambient layer**, never over window content.
- Glass token values are fixed (from spec): `GlassSurface #C9151518`, `GlassSurfaceStrong #E6151518`, `GlassSurfaceWeak #A6151518`, `GlassHighlight #33FFFFFF`, `GlassBorder #2EFFFFFF`, `AmbientBg #0B0B0E`, `AccentIndigo #5865F2`, `AccentPlay #10B981`, `AccentStop #DC2626`, `TextTitle #FFFFFF`, `TextBody #C9CBD6`, `TextMuted #8A8D9A`.
- Motion rhythm: hover 160ms, state 200ms, entrance 220ms. Ambient loops 60–90s. Shimmer 1.4s. Pill pulse 2s.
- Keep `UseLayoutRounding="True"` and `SnapsToDevicePixels="True"` on glass borders.
- No watermark/placeholder behavior is introduced (existing fields keep their current mechanism).
- There is no test project in this repo. Each task verifies via `dotnet build` (0 errors) and a short app launch smoke test (`Start-Process`, wait 3s, confirm no crash).
- `git commit` after each task with a descriptive message. Do not push unless asked.

---

### Task 1: Glass token palette

**Files:**
- Modify: `Among Launcher/Among Launcher/App.xaml:18-49` (palette block)

**Interfaces:**
- Produces: brush/color resource keys consumed by all later tasks: `GlassSurface`, `GlassSurfaceStrong`, `GlassSurfaceWeak`, `GlassHighlight`, `GlassBorder`, `AmbientBg`, `AccentIndigo`, `AccentPlay`, `AccentStop`, `TextTitle`, `TextBody`, `TextMuted`, plus an edge gradient `GlassEdgeBorder`.

- [ ] **Step 1: Replace the matte palette block**

In `App.xaml`, replace the block from `<!-- Matte Dark Palette -->` through the closing `</SolidColorBrush>` of `TextSecondary` (lines 18–49) with:

```xml
            <!-- Glass Palette -->
            <Color x:Key="GlassSurfaceColor">#C9151518</Color>
            <Color x:Key="GlassSurfaceStrongColor">#E6151518</Color>
            <Color x:Key="GlassSurfaceWeakColor">#A6151518</Color>
            <Color x:Key="GlassHighlightColor">#33FFFFFF</Color>
            <Color x:Key="GlassBorderColor">#2EFFFFFF</Color>
            <Color x:Key="AmbientBgColor">#0B0B0E</Color>
            <Color x:Key="AccentColor">#5865F2</Color>
            <Color x:Key="AccentHoverColor">#4752C4</Color>
            <Color x:Key="AccentPressedColor">#3C45A5</Color>
            <Color x:Key="PlayColor">#10B981</Color>
            <Color x:Key="PlayHoverColor">#059669</Color>
            <Color x:Key="StopColor">#DC2626</Color>
            <Color x:Key="StopHoverColor">#B91C1C</Color>
            <Color x:Key="TextTitleColor">#FFFFFF</Color>
            <Color x:Key="TextBodyColor">#C9CBD6</Color>
            <Color x:Key="TextMutedColor">#8A8D9A</Color>
            <Color x:Key="NavIconColor">#A1A1AA</Color>
            <Color x:Key="HeaderColor">#8B949E</Color>

            <SolidColorBrush x:Key="AppBackground" Color="{StaticResource AmbientBgColor}"/>
            <SolidColorBrush x:Key="GlassSurface" Color="{StaticResource GlassSurfaceColor}"/>
            <SolidColorBrush x:Key="GlassSurfaceStrong" Color="{StaticResource GlassSurfaceStrongColor}"/>
            <SolidColorBrush x:Key="GlassSurfaceWeak" Color="{StaticResource GlassSurfaceWeakColor}"/>
            <SolidColorBrush x:Key="GlassHighlight" Color="{StaticResource GlassHighlightColor}"/>
            <SolidColorBrush x:Key="GlassBorder" Color="{StaticResource GlassBorderColor}"/>
            <SolidColorBrush x:Key="AccentBrush" Color="{StaticResource AccentColor}"/>
            <SolidColorBrush x:Key="TextTitle" Color="{StaticResource TextTitleColor}"/>
            <SolidColorBrush x:Key="TextBody" Color="{StaticResource TextBodyColor}"/>
            <SolidColorBrush x:Key="TextMuted" Color="{StaticResource TextMutedColor}"/>
            <SolidColorBrush x:Key="NavIconBrush" Color="{StaticResource NavIconColor}"/>
            <SolidColorBrush x:Key="HeaderBrush" Color="{StaticResource HeaderColor}"/>
            <SolidColorBrush x:Key="TextPrimary" Color="{StaticResource TextTitleColor}"/>
            <SolidColorBrush x:Key="TextSecondary" Color="{StaticResource TextMutedColor}"/>

            <!-- Glass edge: bright 1px top lip fading to subtle -->
            <LinearGradientBrush x:Key="GlassEdgeBorder" StartPoint="0,0" EndPoint="0,1">
                <GradientStop Color="#4DFFFFFF" Offset="0"/>
                <GradientStop Color="#2EFFFFFF" Offset="0.08"/>
                <GradientStop Color="#1AFFFFFF" Offset="1"/>
            </LinearGradientBrush>
```

Keep `SidebarBackground` if still referenced elsewhere, or remove it in Task 4 once the sidebar switches to `GlassSurfaceStrong`.

- [ ] **Step 2: Verify it builds**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: Build succeeds, 0 errors. (`SurfaceCard`/`InputBox`/old brush keys may still reference removed keys — fix any missing-resource compile errors by keeping the removed keys until Task 3 rewrites the styles.)

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Among Launcher/App.xaml"
git commit -m "style: add glass token palette"
```

---

### Task 2: AmbientBackground control

**Files:**
- Create: `Among Launcher/Among Launcher/Views/AmbientBackground.xaml`
- Create: `Among Launcher/Among Launcher/Views/AmbientBackground.xaml.cs`
- Modify: `Among Launcher/Among Launcher/MainWindow.xaml` (root content)

**Interfaces:**
- Consumes: `AccentIndigo` brush resource, `App.ReduceMotion` static (defined in Task 2).
- Produces: `Views.AmbientBackground` UserControl — renders the opaque base + two slowly drifting indigo radial glows. Used as the first child of the MainWindow root grid so all views render over it. Drift is driven by code-behind `BeginAnimation`, not a storyboard resource.

- [ ] **Step 1: Create the control XAML**

`Views/AmbientBackground.xaml`:

```xml
<UserControl x:Class="AmongLauncher.Views.AmbientBackground"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             mc:Ignorable="d" d:DesignHeight="600" d:DesignWidth="900">
    <Grid Background="{StaticResource AppBackground}" ClipToBounds="True">
        <Ellipse x:Name="GlowTop" Width="640" Height="480"
                 HorizontalAlignment="Left" VerticalAlignment="Top"
                 Margin="-120,-160,0,0" Opacity="0.07">
            <Ellipse.Fill>
                <RadialGradientBrush Center="0.5,0.5" RadiusX="0.5" RadiusY="0.5">
                    <GradientStop Color="#5865F2" Offset="0"/>
                    <GradientStop Color="#005865F2" Offset="1"/>
                </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform>
                <TranslateTransform x:Name="GlowTopTransform"/>
            </Ellipse.RenderTransform>
            <Ellipse.Effect>
                <BlurEffect Radius="80"/>
            </Ellipse.Effect>
        </Ellipse>
        <Ellipse x:Name="GlowBottom" Width="560" Height="420"
                 HorizontalAlignment="Right" VerticalAlignment="Bottom"
                 Margin="0,0,-100,-140" Opacity="0.05">
            <Ellipse.Fill>
                <RadialGradientBrush Center="0.5,0.5" RadiusX="0.5" RadiusY="0.5">
                    <GradientStop Color="#5865F2" Offset="0"/>
                    <GradientStop Color="#005865F2" Offset="1"/>
                </RadialGradientBrush>
            </Ellipse.Fill>
            <Ellipse.RenderTransform>
                <TranslateTransform x:Name="GlowBottomTransform"/>
            </Ellipse.RenderTransform>
            <Ellipse.Effect>
                <BlurEffect Radius="80"/>
            </Ellipse.Effect>
        </Ellipse>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Create the code-behind**

`Views/AmbientBackground.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace AmongLauncher.Views;

public partial class AmbientBackground : UserControl
{
    public AmbientBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Drift slowly if reduce-motion is off; otherwise keep static (opacity fades only).
        if (!App.ReduceMotion)
        {
            var topAnim = new DoubleAnimation(0, 40, new Duration(TimeSpan.FromSeconds(80)))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            };
            var bottomAnim = new DoubleAnimation(0, -36, new Duration(TimeSpan.FromSeconds(90)))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
            };
            GlowTopTransform.BeginAnimation(TranslateTransform.YProperty, topAnim);
            GlowBottomTransform.BeginAnimation(TranslateTransform.YProperty, bottomAnim);
        }
    }
}
```

Note: `App.ReduceMotion` is added in Task 9; until then the code references it. To keep Task 2 buildable in isolation, add a temporary static now and finalize its value in Task 9:

```csharp
// App.xaml.cs
public partial class App
{
    public static bool ReduceMotion { get; set; }
}
```

- [ ] **Step 3: Wire into MainWindow root**

In `MainWindow.xaml`, inside the root `<Grid>` (the one with the two RowDefinitions), add as the **first** child (before the title-bar Grid):

```xml
        <!-- Ambient glass layer behind everything -->
        <views:AmbientBackground Grid.RowSpan="2" Grid.ColumnSpan="2"/>
```

- [ ] **Step 4: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: launch the exe, wait 3s, confirm process stays alive, then kill it.

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Among Launcher/Views/AmbientBackground.xaml" "Among Launcher/Among Launcher/Views/AmbientBackground.xaml.cs" "Among Launcher/Among Launcher/App.xaml.cs" "Among Launcher/Among Launcher/MainWindow.xaml"
git commit -m "feat: add ambient glass background layer"
```

---

### Task 3: Shared storyboards + GlassCard + GlassInput + button glow system

**Files:**
- Modify: `Among Launcher/Among Launcher/App.xaml` (styles: SurfaceCard → GlassCard, InputBox → GlassInput, PlayButton, StopButton, SaveButton, SecondaryButton, DiscordButton, IconButton, NavButton, TitleBarButton)

**Interfaces:**
- Consumes: Task 1 tokens (`GlassSurface`, `GlassEdgeBorder`, `GlassSurfaceWeak`, `GlassHighlight`, `TextMuted`, accent brushes).
- Produces: Storyboard keys `HoverEnterGlow`, `HoverExitGlow`, `PressScale`, `ReleaseScale`, `FadeInUp`, `ScalePop`; styles `GlassCard`, `GlassInput`, and upgraded button styles. Later tasks reference these by name.

- [ ] **Step 1: Add shared storyboard resources**

Immediately after the icon geometries block (before the palette), insert:

```xml
            <!-- Shared motion resources -->
            <Storyboard x:Key="HoverEnterGlow">
                <DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="BlurRadius" To="18" Duration="0:0:0.18"/>
                <DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="Opacity" To="0.55" Duration="0:0:0.18"/>
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleX" To="1.02" Duration="0:0:0.2"/>
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleY" To="1.02" Duration="0:0:0.2"/>
            </Storyboard>
            <Storyboard x:Key="HoverExitGlow">
                <DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="BlurRadius" To="0" Duration="0:0:0.18"/>
                <DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="Opacity" To="0" Duration="0:0:0.18"/>
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleX" To="1" Duration="0:0:0.2"/>
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleY" To="1" Duration="0:0:0.2"/>
            </Storyboard>
            <Storyboard x:Key="PressScale">
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleX" To="0.98" Duration="0:0:0.12"/>
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleY" To="0.98" Duration="0:0:0.12"/>
                <DoubleAnimation Storyboard.TargetName="Glow" Storyboard.TargetProperty="Opacity" To="0.25" Duration="0:0:0.12"/>
            </Storyboard>
            <Storyboard x:Key="ReleaseScale">
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleX" To="1" Duration="0:0:0.16"/>
                <DoubleAnimation Storyboard.TargetName="Scale" Storyboard.TargetProperty="ScaleY" To="1" Duration="0:0:0.16"/>
            </Storyboard>
            <Storyboard x:Key="FadeInUp">
                <DoubleAnimation Storyboard.TargetProperty="Opacity" From="0" To="1" Duration="0:0:0.22"/>
                <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" From="8" To="0" Duration="0:0:0.22"/>
            </Storyboard>
```

> `FadeInUp` targets `(UIElement.RenderTransform).(TranslateTransform.Y)` with no
> `TargetName`, so it animates whichever element hosts the `EventTrigger`. Every
> element that runs it (popups, modal cards, welcome elements) **must** have a
> `RenderTransform` of type `TranslateTransform` on itself, or the second
> animation silently no-ops (Opacity still animates). Tasks 5, 6, and 8 set that
> transform.

- [ ] **Step 2: Rewrite `SurfaceCard` as `GlassCard`**

Replace the `SurfaceCard` style with:

```xml
            <Style x:Key="GlassCard" TargetType="Border">
                <Setter Property="Background" Value="{StaticResource GlassSurface}"/>
                <Setter Property="BorderBrush" Value="{StaticResource GlassEdgeBorder}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="CornerRadius" Value="12"/>
                <Setter Property="Padding" Value="20"/>
                <Setter Property="Margin" Value="0,0,0,16"/>
                <Setter Property="Opacity" Value="0.97"/>
                <Setter Property="SnapsToDevicePixels" Value="True"/>
                <Setter Property="UseLayoutRounding" Value="True"/>
                <Setter Property="Effect">
                    <Setter.Value>
                        <DropShadowEffect Color="#000000" Opacity="0.35" BlurRadius="24" ShadowDepth="6"/>
                    </Setter.Value>
                </Setter>
            </Style>
```

Keep `SurfaceCard` as an alias that points to the same setters (or leave a compat style) only if other files still reference it — the plan removes those references in Tasks 5–8. If `SurfaceCard` is still referenced after this task, keep a thin `BasedOn` alias:

```xml
            <Style x:Key="SurfaceCard" TargetType="Border" BasedOn="{StaticResource GlassCard}"/>
```

- [ ] **Step 3: Rewrite `InputBox` as `GlassInput`**

Rename the style key to `GlassInput` and replace the entire style (all setters plus the `ControlTemplate`) with:

```xml
            <Style x:Key="GlassInput" TargetType="TextBox">
                <Setter Property="Background" Value="{StaticResource GlassSurfaceWeak}"/>
                <Setter Property="Foreground" Value="{StaticResource TextTitle}"/>
                <Setter Property="BorderBrush" Value="{StaticResource GlassBorder}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="12"/>
                <Setter Property="FontSize" Value="14"/>
                <Setter Property="CaretBrush" Value="{StaticResource TextTitle}"/>
                <Setter Property="SnapsToDevicePixels" Value="True"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="TextBox">
                            <Border x:Name="Border" Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}"
                                    CornerRadius="8" SnapsToDevicePixels="True" UseLayoutRounding="True">
                                <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsKeyboardFocused" Value="True">
                                    <Setter TargetName="Border" Property="BorderBrush" Value="#B35865F2"/>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>
```

Update any remaining references from `InputBox` to `GlassInput` (SettingsView in Task 7). To keep the build green in the meantime, also add a compat alias immediately after the `GlassInput` style:

```xml
            <Style x:Key="InputBox" TargetType="TextBox" BasedOn="{StaticResource GlassInput}"/>
```

Task 9 removes this alias after all references are swept.

- [ ] **Step 4: Add glow + scale to PlayButton**

Replace the PlayButton `ControlTemplate` with a glow-enabled one (green glow):

```xml
                        <ControlTemplate TargetType="Button">
                            <Border x:Name="B" Background="{TemplateBinding Background}" CornerRadius="8"
                                    SnapsToDevicePixels="True" UseLayoutRounding="True">
                                <Border.RenderTransform>
                                    <ScaleTransform x:Name="Scale" ScaleX="1" ScaleY="1"/>
                                </Border.RenderTransform>
                                <Border.Effect>
                                    <DropShadowEffect x:Name="Glow" Color="#10B981" BlurRadius="0" ShadowDepth="0" Opacity="0"/>
                                </Border.Effect>
                                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"
                                                  Margin="{TemplateBinding Padding}"/>
                            </Border>
                            <ControlTemplate.Triggers>
                                <Trigger Property="IsMouseOver" Value="True">
                                    <Trigger.EnterActions><BeginStoryboard Storyboard="{StaticResource HoverEnterGlow}"/></Trigger.EnterActions>
                                    <Trigger.ExitActions><BeginStoryboard Storyboard="{StaticResource HoverExitGlow}"/></Trigger.ExitActions>
                                </Trigger>
                                <Trigger Property="IsPressed" Value="True">
                                    <Setter TargetName="B" Property="Background" Value="#047857"/>
                                    <Trigger.EnterActions><BeginStoryboard Storyboard="{StaticResource PressScale}"/></Trigger.EnterActions>
                                    <Trigger.ExitActions><BeginStoryboard Storyboard="{StaticResource ReleaseScale}"/></Trigger.ExitActions>
                                </Trigger>
                            </ControlTemplate.Triggers>
                        </ControlTemplate>
```

- [ ] **Step 5: Apply the same glow template to StopButton, SaveButton, SecondaryButton, DiscordButton**

For each, copy the Step 4 template and change:
- `DropShadowEffect Color`:
  - StopButton → `#DC2626`
  - SaveButton → `#10B981`
  - DiscordButton → `#5865F2`
  - SecondaryButton → `#E5E7EB` (neutral white-gray glow; keep hover bg `#2E2E35`, pressed unchanged)
- Keep `CornerRadius` 8 (Discord keeps 5 per brand).

- [ ] **Step 6: Update IconButton / NavButton hover to add Y translate**

In both styles' `IsMouseOver` EnterActions, add:

```xml
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="-1" Duration="0:0:0.12"/>
```

and in ExitActions add the reverse to `0`. Give the template Border a `RenderTransform`:

```xml
                                <Border.RenderTransform><TranslateTransform/></Border.RenderTransform>
```

- [ ] **Step 7: TitleBarButton hairline hover**

Keep as-is functionally; change hover Background to `GlassSurfaceWeak` and Foreground to `TextTitle`.

- [ ] **Step 8: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: launch exe 3s, confirm alive, kill.

- [ ] **Step 9: Commit**

```bash
git add "Among Launcher/Among Launcher/App.xaml"
git commit -m "style: glass cards, inputs, and button glow system"
```

---

### Task 4: MainWindow shell (title bar, sidebar, status pill, avatar)

**Files:**
- Modify: `Among Launcher/Among Launcher/MainWindow.xaml`

**Interfaces:**
- Consumes: `AmbientBackground`, `GlassSurfaceStrong`, `GlassSurface`, `GlassEdgeBorder`, `GlassBorder`, `AccentIndigo`, `AccentStop`, `PillPulse` (add storyboard locally in this file).
- Produces: glassy shell consistent across views.

- [ ] **Step 1: Sidebar → glass, width 76**

- Change `ColumnDefinition Width="72"` to `Width="76"`.
- Change the Sidebar `Border` to:
  - `Background="{StaticResource GlassSurfaceStrong}"`
  - `BorderBrush="{StaticResource GlassBorder}"`, `BorderThickness="0,0,1,0"`
  - keep the drop shadow effect.

- [ ] **Step 2: Title bar → glass**

- Change the title-bar `Grid` Background from `#08080A` to `{StaticResource GlassSurfaceStrong}`, and add a bottom hairline by setting `BorderBrush="{StaticResource GlassBorder}"` + `BorderThickness="0,0,0,1"`.

- [ ] **Step 3: Status pill**

- Change the `StatusBadge` Border to `Background="{StaticResource GlassSurface}"`, `BorderBrush="{StaticResource GlassBorder}"`.
- Add a `PillPulse` storyboard in the window's resources (or as a resource dictionary entry) that pulses the `StatusBadge` `DropShadowEffect` (red `#DC2626`) blur 0→16→0 over 2s `RepeatBehavior="Forever"`; only started from code-behind when the game is running (Task is presentation-only; the existing `UpdateStatusBadge` sets colors — keep that, and add a `DropShadowEffect` with `Opacity=0` that the pulse animates).

- [ ] **Step 4: Avatar glow ring**

- Wrap the avatar `Grid` (row 4) with a `DropShadowEffect` (`Color #5865F2`, `Opacity 0`, `BlurRadius 14`) and, in code-behind `MainWindow.xaml.cs`, animate it to `Opacity 0.6` on `MouseEnter` of the avatar grid and back to `0` on `MouseLeave` (or set via an `EventTrigger` in XAML). Prefer XAML `EventTrigger` with a small inline storyboard.

- [ ] **Step 5: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: launch exe 3s, confirm alive, kill.

- [ ] **Step 6: Commit**

```bash
git add "Among Launcher/Among Launcher/MainWindow.xaml" "Among Launcher/Among Launcher/MainWindow.xaml.cs"
git commit -m "style: glass shell, sidebar, status pill, avatar glow"
```

---

### Task 5: MainView (Home) glass

**Files:**
- Modify: `Among Launcher/Among Launcher/Views/MainView.xaml`

**Interfaces:**
- Consumes: `GlassCard`, `GlassInput`, `GlassSurfaceWeak`, `GlassHighlight`, `AccentIndigo`, `TextBody`, `TextMuted`, `FadeInUp`, `ScalePop`, upgraded button styles.
- Produces: glass home view.

- [ ] **Step 1: Replace all `SurfaceCard` → `GlassCard`**

Replace the three `Style="{StaticResource SurfaceCard}"` uses with `Style="{StaticResource GlassCard}"`.

- [ ] **Step 2: Replace hardcoded colors with tokens**

Sweep `MainView.xaml` and replace:
- `Foreground="#A1A1AA"` → `{StaticResource TextBody}`
- `Foreground="#6B6B76"` → `{StaticResource TextMuted}`
- `Foreground="#FFFFFF"` → `{StaticResource TextTitle}`
- `Background="#1A1A1E"` (cards/rows/popup bg) → `{StaticResource GlassSurfaceWeak}`
- `BorderBrush="#242429"` → `{StaticResource GlassBorder}`
- `Background="#242429"` (progress track) → `{StaticResource GlassSurfaceWeak}`

Do not change semantic accent colors (`#10B981` play, `#DC2626` stop, `#5865F2` progress foreground).

- [ ] **Step 3: Hero cover glow + entrance**

- Add a `DropShadowEffect` (`Color #5865F2`, `Opacity 0.35`, `BlurRadius 22`) to the hero cover `Border`.
- Add a `Loaded` `EventTrigger` on the hero cover that runs `ScalePop` (define a local storyboard animating the cover `ScaleTransform` 0.94→1 and the glow Opacity 0→0.35 over 400ms).

- [ ] **Step 4: Add Mod popup**

- Change the popup `Border` to `Background="{StaticResource GlassSurfaceWeak}"`, `BorderBrush="{StaticResource GlassBorder}"`, `CornerRadius="10"`.
- Add `FadeInUp`-style entrance: give the popup content a `RenderTransform` `TranslateTransform` and a `Loaded` trigger running `FadeInUp`.
- Preset/import buttons already use `SecondaryButton` — they inherit the glow.

- [ ] **Step 5: Local mod rows**

- Change row `Border` background to `{StaticResource GlassSurfaceWeak}`, border `{StaticResource GlassBorder}`, `CornerRadius="8"`. Add a hover `DropShadowEffect` via `Border` `Style.Triggers` `IsMouseOver` (neutral glow).
- Keep the Remove button using `SecondaryButton` (add `Foreground="#DC2626"` on the Remove button so dismiss reads red).

- [ ] **Step 6: Progress bar**

- Change `MainProgressBar` track `Background` to `{StaticResource GlassSurfaceWeak}`; keep `Foreground="#5865F2"`. Set `Height="8"`. (Shimmer is applied to the download modal only, per spec.)

- [ ] **Step 7: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: launch exe 3s, confirm alive, kill.

- [ ] **Step 8: Commit**

```bash
git add "Among Launcher/Among Launcher/Views/MainView.xaml"
git commit -m "style: glass home view with hero glow"
```

---

### Task 6: WelcomeView glass + entrance

**Files:**
- Modify: `Among Launcher/Among Launcher/Views/WelcomeView.xaml`

**Interfaces:**
- Consumes: `TextPrimary`, `TextSecondary`, `ScalePop`-style storyboard (define locally), `FadeInUp`-style storyboard (define locally), `DiscordButton`.

- [ ] **Step 1: Restyle title + subtitle**

- Keep the centered bloom ellipse (already `#665865F2` radial); bump `Ellipse.Opacity` to `0.35` and add a slow `Opacity` breathe loop (2s `AutoReverse` `RepeatBehavior=Forever`, 0.35↔0.28) via a local storyboard on `Loaded`.
- Title: keep `FontSize=46`, `TextPrimary`, glow `#5865F2` blur 14. Add `ScalePop` entrance: a `RenderTransform` `ScaleTransform` (0.94) + glow opacity 0 animated to 1 / 0.35 over 400ms on `Loaded`.
- Subtitle and Discord button: `FadeInUp` entrance with a 120ms `BeginTime` delay on the subtitle, 180ms on the button.

- [ ] **Step 2: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: force welcome by clearing `%LocalAppData%\AmongLauncher` (or just launch; verify no crash).

- [ ] **Step 3: Commit**

```bash
git add "Among Launcher/Among Launcher/Views/WelcomeView.xaml"
git commit -m "style: glass welcome entrance"
```

---

### Task 7: SettingsView glass

**Files:**
- Modify: `Among Launcher/Among Launcher/Views/SettingsView.xaml`

**Interfaces:**
- Consumes: `GlassCard`, `GlassInput`, `TextTitle`, `TextBody`, `TextMuted`, `IconButton`.

- [ ] **Step 1: Replace cards and input**

- Replace the three `SurfaceCard` uses with `GlassCard`.
- Replace the `ServerUrlTextBox` style `InputBox` → `GlassInput`.
- Replace hardcoded text colors (`#A1A1AA` → `TextBody`, `#6B6B76` → `TextMuted`, `#FFFFFF` → `TextTitle`, `#2a2a38`/`#0d0d14` field colors → `GlassSurfaceWeak`/`GlassBorder`).

- [ ] **Step 2: Icon buttons**

- Browse (folder, white) and Reset (trash, `#DC2626`) already use `IconButton`; keep the folder white and trash red. IconButton already has hover glow (Task 3 adds the Y translate).

- [ ] **Step 3: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: launch, navigate to Settings via the nav button, confirm no crash (manual).

- [ ] **Step 4: Commit**

```bash
git add "Among Launcher/Among Launcher/Views/SettingsView.xaml"
git commit -m "style: glass settings view"
```

---

### Task 8: Modal overlay + modals

**Files:**
- Modify: `Among Launcher/Among Launcher/Views/ModalOverlay.xaml`
- Modify: `Among Launcher/Among Launcher/Views/DownloadModsModal.xaml`
- Modify: `Among Launcher/Among Launcher/Views/ConfirmationModal.xaml`
- Modify: `Among Launcher/Among Launcher/Views/PresetModLibraryModal.xaml`
- Modify: `Among Launcher/Among Launcher/Views/LogViewerModal.xaml`

**Interfaces:**
- Consumes: `GlassSurfaceStrong`, `GlassEdgeBorder`, `GlassSurfaceWeak`, `GlassBorder`, `TextBody`, `TextMuted`, `FadeInUp`-style storyboard (define locally), `Shimmer` (define locally in DownloadModsModal).

- [ ] **Step 1: ModalOverlay card**

- Modal card `Border`: `Background="{StaticResource GlassSurfaceStrong}"`, `BorderBrush="{StaticResource GlassEdgeBorder}"`, `CornerRadius="14"`, keep drop shadow.
- Backdrop `Rectangle`: keep `Fill="#000000"`, `Opacity="0.7"` (unchanged).
- Add a `Loaded` entrance: give the card a `RenderTransform` `TranslateTransform` + run `FadeInUp` (Opacity 0→1, Y 8→0, 220ms).

- [ ] **Step 2: DownloadModsModal rows + shimmer**

- Row `Border` → `Background="{StaticResource GlassSurfaceWeak}"`, `BorderBrush="{StaticResource GlassBorder}"`, `CornerRadius="8"`.
- Progress bar `Foreground="#10B981"`, `Background="{StaticResource GlassSurfaceWeak}"`.
- Add a `Shimmer` storyboard to the progress `Fill`/`Foreground`? `ProgressBar` uses `Foreground`. Instead, animate `ProgressBar.Opacity` 1↔0.55 over 1.4s `AutoReverse` `RepeatBehavior=Forever` on the active row (bind a `Style.Trigger` on `IsActive`, or toggle in code-behind). Simplest: in `StartAsync`, before setting `item.IsActive = true`, run the opacity storyboard on the row's progress bar. For plan clarity: set `ProgressBar` `Opacity` animation via `DataTrigger` on `IsActive` in the row template:

```xml
<DataTemplate.Triggers>
    <DataTrigger Binding="{Binding IsActive}" Value="True">
        <DataTrigger.EnterActions>
            <BeginStoryboard>
                <Storyboard>
                    <DoubleAnimation Storyboard.TargetProperty="(UIElement.Opacity)" From="1" To="0.55"
                                     Duration="0:0:0.7" AutoReverse="True" RepeatBehavior="Forever"/>
                </Storyboard>
            </BeginStoryboard>
        </DataTrigger.EnterActions>
    </DataTrigger>
</DataTemplate.Triggers>
```

Target the row root Border's `Opacity`.

- [ ] **Step 3: ConfirmationModal / PresetModLibraryModal / LogViewerModal**

- These are content hosted inside the ModalOverlay card (which is now glass). Update their internal borders/text to tokens:
  - `ConfirmationModal`: `MessageText` → `TextBody`; buttons already `SecondaryButton` (Cancel) and red Confirm — keep.
  - `PresetModLibraryModal`: preset rows → `GlassSurfaceWeak`/`GlassBorder`; names `TextTitle`, desc `TextBody`; Install button stays `SaveButton`.
  - `LogViewerModal`: log panel Border → `Background="{StaticResource GlassSurfaceWeak}"`, `BorderBrush="{StaticResource GlassBorder}"`; log text → `TextBody`.

- [ ] **Step 4: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors.
Smoke test: launch, open the preset library / logs modals via the Home view, confirm no crash (manual).

- [ ] **Step 5: Commit**

```bash
git add "Among Launcher/Among Launcher/Views/ModalOverlay.xaml" "Among Launcher/Among Launcher/Views/DownloadModsModal.xaml" "Among Launcher/Among Launcher/Views/ConfirmationModal.xaml" "Among Launcher/Among Launcher/Views/PresetModLibraryModal.xaml" "Among Launcher/Among Launcher/Views/LogViewerModal.xaml"
git commit -m "style: glass modals with entrance and shimmer"
```

---

### Task 9: Reduce-motion gate + leftover color sweep

**Files:**
- Modify: `Among Launcher/Among Launcher/App.xaml.cs`
- Modify: any `.xaml` still referencing removed matte keys or hardcoded matte hex.

**Interfaces:**
- Consumes: the `App.ReduceMotion` static added in Task 2 (finalize it here).
- Produces: a single source of truth for motion preference.

- [ ] **Step 1: Finalize `App.ReduceMotion`**

`App.xaml.cs`:

```csharp
using System.Windows;

namespace AmongLauncher;

public partial class App
{
    public static bool ReduceMotion { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        ReduceMotion = !SystemParameters.ClientAreaAnimation;
        base.OnStartup(e);
    }
}
```

Keep the `ReduceMotion` getter used by `AmbientBackground` (Task 2) and gate `ScalePop`/`PillPulse`/shimmer where feasible by checking `App.ReduceMotion` in code-behind (XAML storyboard triggers can't read it; keep those loops behind the `Loaded` code-behind that already starts them).

- [ ] **Step 2: Sweep for leftover matte references**

Grep the `Among Launcher` project for: `SurfaceCard`, `InputBox`, `#1A1A1E`, `#242429`, `#08080A`, `#A1A1AA`, `#6B6B76`, `#2A2A30`, `#0d0d14`, `#2a2a38`. Replace any remaining with the corresponding glass/text token. If `SurfaceCard`/`InputBox` keys are still referenced, keep the `BasedOn` aliases (Task 3) or delete references.

- [ ] **Step 3: Verify**

Run: `dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj"`
Expected: 0 errors, 0 references to matte tokens in XAML.
Smoke test: launch exe 3s, confirm alive, kill.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: reduce-motion gate and token sweep"
```

---

### Task 10: Final verification

**Files:**
- None (verification only).

- [ ] **Step 1: Full clean build**

```bash
dotnet build "Among Launcher/Among Launcher/Among Launcher.csproj" -c Release
```
Expected: 0 errors.

- [ ] **Step 2: Launch smoke test**

Launch the built exe, wait 4s, confirm the process stays alive, then kill it.

- [ ] **Step 3: Manual visual pass (human)**

Confirm: hover glows on all buttons, glass cards with bright top edge, ambient background drift, status pill, welcome entrance, modal entrance + download shimmer, and that text remains legible. Confirm Reduce Motion is honored by checking the system setting path.

- [ ] **Step 4: Commit any stragglers**

```bash
git add -A
git commit -m "chore: final glass overhaul verification"
```
