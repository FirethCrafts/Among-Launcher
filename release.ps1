# release.ps1 — Build and release the Tauri launcher
#
# Usage:
#   ./release.ps1              # Bump patch version (1.0.0 → 1.0.1)
#   ./release.ps1 -Minor       # Bump minor version (1.0.0 → 1.1.0)
#   ./release.ps1 -Major       # Bump major version (1.0.0 → 2.0.0)
#   ./release.ps1 -Version 1.2.3  # Set specific version

param(
    [switch]$Minor,
    [switch]$Major,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$tauriConf = "launcher-tauri\src-tauri\tauri.conf.json"
$packageJson = "launcher-tauri\package.json"

# --- Check gh CLI ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "Error: GitHub CLI (gh) not found. Install: https://cli.github.com/" -ForegroundColor Red
    exit 1
}

# --- Get latest LAUNCHER version from GitHub releases (not from local file) ---
# gh release list returns newest-of-any-kind first, so a mod/v* release at
# the head used to defeat the old single-entry regex and fall back to a
# hardcoded 1.0.0 (which seeded bogus launcher/v1.0.1 releases and would
# next run collide on an existing tag). Fetch ~30 releases instead, keep
# only launcher/* tags, and derive the version from those.
Write-Host "Fetching latest launcher release from GitHub..." -ForegroundColor Yellow
$releaseListJson = gh release list --repo "FirethCrafts/Among-Launcher" --limit 30 --json tagName 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to fetch release list from GitHub (gh exit code $LASTEXITCODE) - refusing to guess a version."
}
$launcherTags = @()
if ($releaseListJson) {
    $parsedReleases = $releaseListJson | ConvertFrom-Json
    $launcherTags = @($parsedReleases | Where-Object { $_.tagName -like 'launcher/*' } | ForEach-Object { $_.tagName })
}
$launcherVersions = @()
foreach ($t in $launcherTags) {
    if ($t -match '^launcher/v(.+)$') {
        $launcherVersions += $matches[1]
    }
}
if ($launcherVersions.Count -gt 0) {
    # gh lists releases newest first, so index 0 is the newest launcher release.
    $currentVersion = $launcherVersions[0]
    Write-Host "Latest launcher release: launcher/v$currentVersion" -ForegroundColor Cyan
} elseif ($launcherTags.Count -gt 0) {
    throw "Launcher releases exist but none parse as 'launcher/v<version>' (newest: $($launcherTags[0])) - cannot derive current version."
} else {
    # Seeding 1.0.0 is allowed ONLY when zero launcher releases exist at all.
    $currentVersion = "1.0.0"
    Write-Host "No launcher releases found, starting at $currentVersion" -ForegroundColor Yellow
}
$parts = $currentVersion.Split('.')

# --- Calculate new version ---
if ($Version) {
    $newVersion = $Version
} elseif ($Major) {
    $parts[0] = [int]$parts[0] + 1
    $parts[1] = 0
    $parts[2] = 0
    $newVersion = $parts -join '.'
} elseif ($Minor) {
    $parts[1] = [int]$parts[1] + 1
    $parts[2] = 0
    $newVersion = $parts -join '.'
} else {
    $parts[2] = [int]$parts[2] + 1
    $newVersion = $parts -join '.'
}

Write-Host "Version: $currentVersion -> $newVersion" -ForegroundColor Cyan

# --- Check for uncommitted changes (only tracked files) ---
$gitStatus = git status --porcelain | Where-Object { $_ -match '^(M|A|D|R)' }
if ($gitStatus) {
    Write-Host "Error: Uncommitted changes in tracked files. Commit or stash first." -ForegroundColor Red
    exit 1
}

# --- Bump version in tauri.conf.json (before build!) ---
$content = Get-Content $tauriConf -Raw
$content = $content -replace '"version":\s*"[^"]*"', "`"version`": `"$newVersion`""
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText((Resolve-Path $tauriConf).Path, $content, $utf8NoBom)
Write-Host "Bumped tauri.conf.json to $newVersion" -ForegroundColor Green

# --- Bump version in launcher-tauri/package.json (keep in sync with tauri.conf.json) ---
$pkgContent = Get-Content $packageJson -Raw
$pkgContent = $pkgContent -replace '"version":\s*"[^"]*"', "`"version`": `"$newVersion`""
[System.IO.File]::WriteAllText((Resolve-Path $packageJson).Path, $pkgContent, $utf8NoBom)
Write-Host "Bumped package.json to $newVersion" -ForegroundColor Green

# --- Bump version in src-tauri/Cargo.toml ([package] section only) ---
# Anchored to the [package] section and to a line-start `version =` so the
# dependency versions elsewhere in the file are never touched.
$cargoToml = "launcher-tauri\src-tauri\Cargo.toml"
$cargoLock = "launcher-tauri\src-tauri\Cargo.lock"
$cargoContent = Get-Content $cargoToml -Raw
$cargoContent = [regex]::Replace($cargoContent, '(?ms)(\[package\][^\[]*?^version\s*=\s*)"[^"]*"', ('$1"' + $newVersion + '"'), 1)
[System.IO.File]::WriteAllText((Resolve-Path $cargoToml).Path, $cargoContent, $utf8NoBom)
Write-Host "Bumped Cargo.toml to $newVersion" -ForegroundColor Green

# --- Verify the file was written correctly ---
$verifyConfig = Get-Content $tauriConf -Raw | ConvertFrom-Json
if ($verifyConfig.version -ne $newVersion) {
    Write-Host "Error: tauri.conf.json version mismatch! Expected $newVersion, got $($verifyConfig.version)" -ForegroundColor Red
    exit 1
}
$verifyPkg = Get-Content $packageJson -Raw | ConvertFrom-Json
if ($verifyPkg.version -ne $newVersion) {
    Write-Host "Error: package.json version mismatch! Expected $newVersion, got $($verifyPkg.version)" -ForegroundColor Red
    exit 1
}
$cargoPkgVersion = $null
if ((Get-Content $cargoToml -Raw) -match '(?ms)\[package\][^\[]*?^version\s*=\s*"([^"]+)"') {
    $cargoPkgVersion = $matches[1]
}
if ($cargoPkgVersion -ne $newVersion) {
    Write-Host "Error: Cargo.toml [package] version mismatch! Expected $newVersion, got $cargoPkgVersion" -ForegroundColor Red
    exit 1
}

# --- Clean old build artifacts ---
Write-Host "Cleaning old build artifacts..." -ForegroundColor Yellow
Push-Location "launcher-tauri"
if (Test-Path "src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis") {
    Remove-Item -Recurse -Force "src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis" -ErrorAction SilentlyContinue
}
Pop-Location

# --- Install dependencies ---
Write-Host "Installing dependencies..." -ForegroundColor Yellow
Push-Location "launcher-tauri"
npm ci
if ($LASTEXITCODE -ne 0) { Write-Host "npm ci failed" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location

# --- Build Tauri app ---
Write-Host "Building Tauri app (this takes ~2 min)..." -ForegroundColor Yellow
Push-Location "launcher-tauri"
npx tauri build --target x86_64-pc-windows-msvc --bundles nsis
if ($LASTEXITCODE -ne 0) { Write-Host "Tauri build failed" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location

# --- Find the NSIS installer ---
$nsisDir = "launcher-tauri\src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis"
$installer = Get-ChildItem -Path $nsisDir -Filter "*.exe" | Select-Object -First 1
if (-not $installer) {
    Write-Host "Error: NSIS installer not found in $nsisDir" -ForegroundColor Red
    exit 1
}
Write-Host "Found installer: $($installer.Name)" -ForegroundColor Green

# --- Verify installer version matches ---
if ($installer.Name -notmatch $newVersion.Replace('.', '\.')) {
    Write-Host "Warning: Installer filename $($installer.Name) doesn't contain expected version $newVersion" -ForegroundColor Yellow
}

# --- Commit the version bump BEFORE tagging ---
# gh release create never pushes local commits: with no --target it creates
# the tag server-side from the REMOTE default-branch HEAD. The bump commit
# must therefore be committed AND pushed first, otherwise every release tag
# would keep pointing at the previous version's commit (verified root cause:
# tag launcher/v1.2.10's source tree contained 1.2.9).
git add $tauriConf $packageJson $cargoToml $cargoLock
git commit -m "chore: bump launcher to v$newVersion"
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to commit version bump" -ForegroundColor Red
    exit 1
}

# Rebase onto the remote and retry: the CI workflow also bumps and pushes
# master, so a concurrent run can reject our plain push as non-fast-forward.
# A rejected push here would mean the release tags the wrong commit, so we
# must not give up until the bump is actually on the remote.
$pushed = $false
for ($i = 1; $i -le 5; $i++) {
    git pull --rebase origin master
    if ($LASTEXITCODE -ne 0) {
        # A failed rebase can leave the repo mid-rebase; abort it so we fail
        # with a clear message instead of looping forever on a conflict.
        if ((Test-Path ".git\rebase-merge") -or (Test-Path ".git\rebase-apply")) {
            Write-Host "Error: Rebase conflict while pulling the version bump - aborting rebase" -ForegroundColor Red
            git rebase --abort
            exit 1
        }
        Write-Host "Push attempt $i failed (pull failed); retrying in 5s..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
        continue
    }
    git push origin master
    if ($LASTEXITCODE -eq 0) { $pushed = $true; break }
    Write-Host "Push attempt $i failed (remote moved); retrying in 5s..." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
}
if (-not $pushed) {
    Write-Host "Error: Failed to push version bump after 5 attempts - release would tag the wrong commit" -ForegroundColor Red
    exit 1
}

# --- Create GitHub Release (tags the just-pushed bump commit) ---
$tag = "launcher/v$newVersion"
$releaseName = "Launcher v$newVersion"

Write-Host "Creating GitHub Release: $releaseName" -ForegroundColor Yellow
gh release create $tag `
    --repo "FirethCrafts/Among-Launcher" `
    --title $releaseName `
    --generate-notes `
    $installer.FullName

if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Failed to create GitHub release" -ForegroundColor Red
    exit 1
}
Write-Host "Release created: https://github.com/FirethCrafts/Among-Launcher/releases/tag/$tag" -ForegroundColor Green

# --- Push after the release (original script tail: push happens after
#     release creation; no-op when the pre-release push above succeeded) ---
git push origin master

Write-Host ""
Write-Host "Done! Launcher v$newVersion released." -ForegroundColor Cyan
Write-Host "  Tag: $tag" -ForegroundColor Gray
Write-Host "  Release: https://github.com/FirethCrafts/Among-Launcher/releases/tag/$tag" -ForegroundColor Gray
