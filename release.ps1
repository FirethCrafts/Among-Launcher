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

# --- Read current version ---
$config = Get-Content $tauriConf -Raw | ConvertFrom-Json
$currentVersion = $config.version
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

# --- Check gh CLI ---
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host "Error: GitHub CLI (gh) not found. Install: https://cli.github.com/" -ForegroundColor Red
    exit 1
}

# --- Check for uncommitted changes (only tracked files) ---
$gitStatus = git status --porcelain | Where-Object { $_ -match '^(M|A|D|R)' }
if ($gitStatus) {
    Write-Host "Error: Uncommitted changes in tracked files. Commit or stash first." -ForegroundColor Red
    exit 1
}

# --- Bump version in tauri.conf.json ---
$config.version = $newVersion
$config | ConvertTo-Json -Depth 10 | Set-Content $tauriConf -Encoding UTF8
Write-Host "Bumped tauri.conf.json to $newVersion" -ForegroundColor Green

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

# --- Create GitHub Release ---
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

# --- Commit version bump ---
git add $tauriConf
git commit -m "chore: bump launcher to v$newVersion"
git push origin master

Write-Host ""
Write-Host "Done! Launcher v$newVersion released." -ForegroundColor Cyan
Write-Host "  Tag: $tag" -ForegroundColor Gray
Write-Host "  Release: https://github.com/FirethCrafts/Among-Launcher/releases/tag/$tag" -ForegroundColor Gray
