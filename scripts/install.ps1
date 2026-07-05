# wimux remote installer. Downloads the latest release bundle, extracts it to
# %LOCALAPPDATA%\Programs\wimux and puts wimux on the user PATH.
#
# One-liner:
#   irm https://raw.githubusercontent.com/dat-hoangnguyentuandat/wimux/main/scripts/install.ps1 | iex
#
# Pin a version:
#   $env:WIMUX_VERSION="v0.1.0"; irm .../install.ps1 | iex
#
# Override the source repo with $env:WIMUX_REPO="owner/name".
$ErrorActionPreference = "Stop"

$repo    = if ($env:WIMUX_REPO)    { $env:WIMUX_REPO }    else { "dat-hoangnguyentuandat/wimux" }
$version = if ($env:WIMUX_VERSION) { $env:WIMUX_VERSION } else { "latest" }
$runtime = "win-x64"
$asset   = "wimux-$runtime.zip"
$dest    = Join-Path $env:LOCALAPPDATA "Programs\wimux"

Write-Host "Installing wimux from $repo ($version)..." -ForegroundColor Cyan

# Resolve the download URL via the GitHub API.
$headers = @{ "User-Agent" = "wimux-installer"; "Accept" = "application/vnd.github+json" }
if ($version -eq "latest") {
  $api = "https://api.github.com/repos/$repo/releases/latest"
} else {
  $api = "https://api.github.com/repos/$repo/releases/tags/$version"
}

try {
  $release = Invoke-RestMethod -Uri $api -Headers $headers
} catch {
  throw "Could not query GitHub releases for $repo. $($_.Exception.Message)"
}

$dl = ($release.assets | Where-Object { $_.name -eq $asset } | Select-Object -First 1).browser_download_url
if (-not $dl) {
  throw "Release '$($release.tag_name)' has no asset named '$asset'."
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) "wimux-$([guid]::NewGuid().ToString('N')).zip"
Write-Host "Downloading $asset ..." -ForegroundColor DarkGray
Invoke-WebRequest -Uri $dl -OutFile $tmp -Headers @{ "User-Agent" = "wimux-installer" }

# Stop a running instance so we can overwrite files.
Get-Process wimux -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "Extracting to $dest ..." -ForegroundColor DarkGray
Expand-Archive -Path $tmp -DestinationPath $dest -Force
Remove-Item -Force $tmp

if (-not (Test-Path (Join-Path $dest "wimux.exe"))) {
  throw "Install failed: wimux.exe not found in $dest"
}

# Add to user PATH if missing.
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -notcontains $dest) {
  $newPath = if ([string]::IsNullOrEmpty($userPath)) { $dest } else { "$userPath;$dest" }
  [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
  $env:Path = "$env:Path;$dest"
  Write-Host "Added $dest to your user PATH." -ForegroundColor Green
}

$ver = & (Join-Path $dest "wimux.exe") version
Write-Host ""
Write-Host "Installed $ver" -ForegroundColor Green
Write-Host "Open a NEW terminal and run:  wimux" -ForegroundColor Green
