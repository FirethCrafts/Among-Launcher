# Adapting the Launcher for Epic & MS Store Among Us

## Epic — easy-ish
- Detect install from `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` (`InstallLocation` field).
- Don't write `steam_appid.txt` (Steam-only).
- Launch the copied exe with `-EpicPortal` arg.
- Everything else (copy, BepInEx, plugins) stays the same.

**Caveats:**
- **No version downgrade.** Steam has a `public-previous` beta branch to pin an older game version when a mod needs it; Epic has no such option. If a mod breaks on a newer Among Us, users are stuck.
- **EOS overlay / ownership.** The Epic Online Services overlay may conflict with injection, and the game can check for Epic auth. Launching the copied exe usually works but online features may misbehave.

## MS Store / Xbox App — best effort
- Read game out of `C:\Program Files\WindowsApps\InnerSloth.LLC-AmongUs_*` — requires admin + `takeown`/`icacls` on that one folder (never touch the whole `WindowsApps` dir).
- Use BepInEx **win-x86** (Among Us is x86).
- **Caveat:** the copied exe may not run outside its package. If launch fails, show a clear message: mods are unsupported for this storefront without the Steam/Epic version.

**Caveats (the hard ones):**
- **No downgrade at all.** Store auto-updates the original; you can't pin a version a mod needs.
- **Data lives in package scope.** Saves/config go to `%LOCALAPPDATA%\Packages\InnerSloth.LLC-...`, not the Steam paths. A copied game won't find user data where mods/profiles expect it.
- **AppX activation.** The game is launched by package activation, not by running the exe. Even if the copied exe starts, Xbox/Microsoft-account online play likely won't.
- **VFS layout.** The package uses a virtual file system (`VFS\ProgramFilesX64`, etc.). A bare folder copy may miss files the game resolves through the package; copy the package structure faithfully.

## Shared
- Add a storefront field (`steam` / `epic` / `msstore`) to `LauncherConfig`.
- Branch setup + launch on that field; keep the Steam path as the default.
