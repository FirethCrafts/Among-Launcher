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

# --- Get latest version from GitHub releases (not from local file) ---
Write-Host "Fetching latest release version from GitHub..." -ForegroundColor Yellow
$latestTag = gh release list --repo "FirethCrafts/Among-Launcher" --limit 1 --json tagName --jq '.[0].tagName' 2>$null
if ($latestTag -and $latestTag -match 'launcher/v(.+)') {
    $currentVersion = $matches[1]
    Write-Host "Latest release: $latestTag ($currentVersion)" -ForegroundColor Cyan
} else {
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
git add $tauriConf $packageJson
git commit -m "chore: bump launcher to v$newVersion"
git push origin master

Write-Host ""
Write-Host "Done! Launcher v$newVersion released." -ForegroundColor Cyan
Write-Host "  Tag: $tag" -ForegroundColor Gray
Write-Host "  Release: https://github.com/FirethCrafts/Among-Launcher/releases/tag/$tag" -ForegroundColor Gray
