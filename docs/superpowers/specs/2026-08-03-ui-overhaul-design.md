# AmongLauncher UI Overhaul — Glass Design

**Date:** 2026-08-03
**Status:** Approved (Approach A — Layered Translucency)

## Overview

Rebuild the AmongLauncher WPF interface from a flat matte dashboard into a
glassy/frosted premium launcher with a **neutral-cool frosted shell**, **indigo
(`#5865F2`) primary accent** (green play / red stop retained for actions), and a
layered animation system. The goal is a design that feels alive and polished but
avoids "AI slop" (generic purple gradients, glass-everything, glowing borders on
every element).

**Non-goals:**
- No true OS-level acrylic/behind-window blur (Approach B rejected — Win11-only,
  fragile, breaks custom chrome).
- No redesign of information architecture or launcher logic — this is
  presentation only.
- No new dependencies or NuGet packages.

## Design Principles (anti-"AI slop")

1. **Color is earned.** Frosted surfaces stay neutral-cool. Accent color appears
   only on: the active nav item, primary action (Play), the status pill when
   running, focus/selected states, and link-style buttons. Never as a border on
   every card.
2. **Glass reads through edges, not everywhere.** Each glass surface has a
   brighter 1px top edge (light catching the lip) and a soft drop shadow. Inner
   bulk stays muted.
3. **Motion means something.** Micro-interactions respond to intent (hover,
   press, state change). Ambient motion only on hero/status/welcome — slow and
   low-contrast. No idle animations on content the user is reading.
4. **One "wow" per screen.** Welcome entrance, hero cover glow, and the download
   modal shimmer. Everything else is subtle.

## Palette (glass tokens)

Replace the matte `SolidColorBrush` palette with a glass token set. Opacity is
expressed via 8-digit ARGB hex so surfaces stay separate from the ambient layer.

| Token | Value | Use |
|-------|-------|-----|
| `GlassSurface` | `#C9151518` (~79%) | Card / panel fill |
| `GlassSurfaceStrong` | `#E6151518` (~90%) | Sidebar, modal body, areas needing legibility |
| `GlassSurfaceWeak` | `#A6151518` (~65%) | Hover-reveal layers, popup bg |
| `GlassHighlight` | `#33FFFFFF` | 1px top-edge highlight |
| `GlassBorder` | `#2EFFFFFF` | 1px outer hairline |
| `AmbientBg` | `#0B0B0E` | Window base (opaque) |
| `AccentIndigo` | `#5865F2` | Primary accent (unchanged) |
| `AccentPlay` | `#10B981` | Play / success (unchanged) |
| `AccentStop` | `#DC2626` | Stop / danger (unchanged) |
| `TextTitle` | `#FFFFFF` | Headings |
| `TextBody` | `#C9CBD6` | Primary body (slightly brighter than current `#A1A1AA` for glass contrast) |
| `TextMuted` | `#8A8D9A` | Secondary (brighter than current `#6B6B76` for legibility over translucency) |

Rationale for text bumps: translucency lowers perceived contrast, so body/muted
text lifts ~1 step to keep the WCAG-ish contrast the matte theme had.

## Ambient Background Layer

A single per-window layer behind all views:
- Base: `AmbientBg` opaque fill.
- Two large radial glows (indigo-tinted, ~5–8% opacity, `BlurRadius` 60–90) that
  **drift very slowly** (60–90s loop, `RepeatBehavior="Forever"`), e.g. one
  top-left, one bottom-right, animated with `TranslateTransform`/`Opacity`.
- The welcome screen may use a stronger centered bloom (see WelcomeView).

Implement as a reusable `AmbientBackground` UserControl used as the root child of
the window content grid so every view gets it for free.

## Glass Surface System (`App.xaml` styles)

### `GlassCard` (replaces `SurfaceCard`)
- Background `GlassSurface`, `CornerRadius=12` (up from 8), `Padding=20`.
- A `Border` composition: outer hairline `GlassBorder`, plus an inner 1px
  top-edge highlight line (`GlassHighlight`) drawn as the top row of an overlay
  `Grid` (or a second nested Border offset by -1).
- `DropShadowEffect` (black, `Opacity=0.35`, `BlurRadius=24`, `ShadowDepth=6`).
- Idle `Opacity` 0.97; on hover animates to 1.0 (160ms), the top-edge highlight
  brightens to `#4DFFFFFF`, and the shadow deepens slightly.

### Buttons
- **PlayButton / StopButton / SaveButton / SecondaryButton / DiscordButton** all
  gain a shared hover behavior: a `DropShadowEffect` glow in the button's own
  accent color (`BlurRadius` 0→18, `Opacity` 0→0.55, 180ms), plus a 1.02 scale
  on the border (200ms). Pressed: scale 0.98, glow pulled back.
- Active glow color per button: Play → `AccentPlay`, Stop → `AccentStop`,
  Discord/Indigo → `AccentIndigo`. Secondary keeps a neutral white-gray glow so
  color stays semantic.
- `CornerRadius` bumps to 8 (from 6).

### `GlassInput` (replaces `InputBox`)
- Fill `GlassSurfaceWeak`, hairline `GlassBorder`.
- Focus: hairline animates to `AccentIndigo` at ~70% opacity + soft indigo glow
  (`BlurRadius` 14), 150ms. Placeholder text (where present) uses `TextMuted`.
- No new placeholder/watermark behavior is introduced; existing fields keep
  their current placeholder mechanism.

### Nav / Icon buttons
- Hover glow retained; add a soft `TranslateTransform` Y:-1 on hover (120ms) so
  icons feel tactile. Active nav keeps the rounded `#222226`-style tile but the
  tile fill becomes `GlassSurfaceStrong` with an indigo 3px accent bar.

### Title bar
- Keep custom chrome; make the bar `GlassSurfaceStrong` with a bottom hairline.
- Window controls keep their hover highlight.

## Animation System

A small set of shared, named `Storyboard` resources in `App.xaml` so motion is
consistent and reused:

| Name | Trigger | Effect | Duration |
|------|---------|--------|----------|
| `FadeInUp` | view load / modal open | Opacity 0→1 + TranslateY 8→0 | 220ms |
| `ScalePop` | welcome logo, hero cover | Scale 0.94→1 + glow bloom | 400ms ease-out |
| `HoverGlow*` | button hover enter/exit | glow blur/opacity + scale | 160–200ms |
| `FocusGlow` | input focus/unfocus | border color + glow | 150ms |
| `AmbientDrift` | ambient glows | slow translate/opacity loop | 60–90s forever |
| `Shimmer` | active download row | gradient sweep on the progress bar | 1.4s loop |
| `PillPulse` | status pill when running | red glow pulse + gentle scale | 2s loop |

Rules:
- All enter/exit pairs animate the **same property** to avoid jumps.
- `Duration` uses a shared 150/200/220ms rhythm; hover 160ms, states 200ms,
  entrances 220ms.
- Respect **Reduce Motion**: gate ambient-drift, scale, and pulse animations
  behind `SystemParameters.ClientAreaAnimation` (read once at startup). When
  disabled, keep opacity fades but skip translate/scale/loop animations. No
  launcher config or logic changes — this stays presentation-only.

## Per-View Changes

### MainWindow (shell)
- Root: `AmbientBackground` + `GlassSurfaceStrong` sidebar.
- Sidebar width 72 → **76** (slightly roomier for glow), same icon rail layout.
- Status pill: `GlassSurface` fill, hairline; running state keeps red accent +
  `PillPulse`; idle stays muted. STOP button styling retained.
- Avatar: keep 40px circular clip; add a faint indigo glow ring that intensifies
  on hover.

### MainView (Home)
- Hero: cover gets a persistent soft indigo glow behind it + `ScalePop` on first
  load. "Among Us" title keeps 28px bold, adds a subtle text glow on hover of the
  card area.
- Action buttons: `PlayButton` (green glow), `Install`/`Browse`/`Logs` as
  `SecondaryButton` with neutral glow. `Browse`/`Logs` could become icon+label
  later — out of scope now.
- Cards (Launch Options, Local Mods, Mods & Status): `GlassCard`, hover reveal.
- Add Mod popup: `GlassSurfaceWeak` + `FadeInUp`; preset/import items get hover
  highlight + glow.
- Local mod rows: glass rows with hover reveal; Remove keeps red-dismiss hover.
- Progress bar: replace with an 8px `GlassInput`-style track; active download
  uses `Shimmer`.

### WelcomeView (Login)
- Stronger centered bloom (indigo `#665865F2`-style radial, ~35% opacity) that
  breathes slowly (`AmbientDrift` variant).
- Title + subtitle entrance: `ScalePop` on title, `FadeInUp` (delayed 120ms) on
  subtitle and Discord button.

### SettingsView
- `GlassCard` for the three setting rows; `GlassInput` for the Server URL field.
- Browse/Reset icon buttons: neutral hover glow (Reset keeps red).

### Modals & Overlays
- `ModalOverlay` backdrop: keep black `0.7`; add subtle blur-less darkening only.
- Modal card: `GlassSurfaceStrong`, `CornerRadius=14`, `FadeInUp` on open.
- `DownloadModsModal`: rows use glass; active row shows `Shimmer` on progress.
- `ConfirmationModal` / `PresetModLibraryModal` / `LogViewerModal`: inherit
  glass card + entrance animation.

## Implementation Notes (WPF specifics)

- Use 8-digit ARGB hex colors for translucency tokens.
- `DropShadowEffect` is the only reliable cross-version glow in WPF (no blur of
  live backdrop without OS APIs). Keep `BlurRadius` ≤ 24 and shadow depth small;
  too many simultaneous effects tank frame rate. **Budget:** ambient 2, hero 1,
  status 1, plus hover glows transiently.
- Reusable `AmbientBackground` control; per-view `Storyboard` resources live in
  `App.xaml` and are referenced by `StaticResource`.
- `UseLayoutRounding=True` and `SnapsToDevicePixels=True` on glass borders to
  keep hairlines crisp.
- Motion-reduction gate: read once at startup into an `App` static, consult in
  storyboard triggers where feasible.
- Views currently hardcode matte hex colors inline (e.g. `#1A1A1E`, `#242429`,
  `#A1A1AA`, `#6B6B76`). Sweep these during implementation and replace with the
  new glass/text tokens so nothing falls back to the old flat look.

## Success Criteria

- App builds and runs on the existing target (net10.0-windows) with no new
  dependencies.
- Every view uses the glass system (no leftover matte `SurfaceCard`/`InputBox`
  references).
- Hover glows, entrance animations, and ambient motion are present and smooth
  (60fps-ish; no dropped-frame storms from effect stacking).
- Text remains legible over translucent surfaces.
- Reduce-motion flag disables ambient/scale loops.
