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

function Install-WindowsTerminalProfile {
  param([Parameter(Mandatory = $true)][string]$InstallDir)

  $exe = Join-Path $InstallDir "wimux.exe"
  if (-not (Test-Path $exe)) { return }

  $fragmentDir = Join-Path $env:LOCALAPPDATA "Microsoft\Windows Terminal\Fragments\Wimux"
  New-Item -ItemType Directory -Force -Path $fragmentDir | Out-Null

  $profile = [ordered]@{
    guid = "{b92adc15-a5aa-416e-90e9-f156688820d8}"
    name = "Wimux"
    commandline = "`"$exe`" pwsh"
    startingDirectory = "%USERPROFILE%"
    hidden = $false
  }

  $icon = Join-Path $InstallDir "wwwroot\favicon.svg"
  if (Test-Path $icon) {
    $profile.icon = $icon
  }

  $fragment = [ordered]@{
    profiles = @($profile)
  }
  $fragmentPath = Join-Path $fragmentDir "wimux.json"
  $fragment | ConvertTo-Json -Depth 5 | Set-Content -Path $fragmentPath -Encoding utf8
  Write-Host "Installed Windows Terminal profile: $fragmentPath" -ForegroundColor Green
}

function Install-WindowsTerminalSettingsProfile {
  param([Parameter(Mandatory = $true)][string]$InstallDir)

  $exe = Join-Path $InstallDir "wimux.exe"
  if (-not (Test-Path $exe)) { return }

  $settingsPaths = @(
    (Join-Path $env:LOCALAPPDATA "Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json"),
    (Join-Path $env:LOCALAPPDATA "Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json"),
    (Join-Path $env:LOCALAPPDATA "Microsoft\Windows Terminal\settings.json")
  ) | Select-Object -Unique

  foreach ($settingsPath in $settingsPaths) {
    if (-not (Test-Path $settingsPath)) { continue }

    try {
      $json = Get-Content -Raw $settingsPath | ConvertFrom-Json

      if (-not $json.profiles) {
        $json | Add-Member -MemberType NoteProperty -Name profiles -Value ([pscustomobject]@{})
      }
      if (-not $json.profiles.list) {
        $json.profiles | Add-Member -MemberType NoteProperty -Name list -Value @()
      }

      $guid = "{b92adc15-a5aa-416e-90e9-f156688820d8}"
      $profile = [ordered]@{
        guid = $guid
        name = "Wimux"
        commandline = "`"$exe`" pwsh"
        startingDirectory = "%USERPROFILE%"
        hidden = $false
      }

      $icon = Join-Path $InstallDir "wwwroot\favicon.svg"
      if (Test-Path $icon) {
        $profile.icon = $icon
      }

      $json.profiles.list = @($json.profiles.list | Where-Object { $_.guid -ne $guid -and $_.name -ne "Wimux" }) + @([pscustomobject]$profile)
      Copy-Item -Force $settingsPath "$settingsPath.wimux.bak"
      $json | ConvertTo-Json -Depth 100 | Set-Content -Path $settingsPath -Encoding utf8
      Write-Host "Updated Windows Terminal settings profile: $settingsPath" -ForegroundColor Green
    } catch {
      Write-Host "Could not update Windows Terminal settings profile: $settingsPath" -ForegroundColor Yellow
      Write-Host $_.Exception.Message -ForegroundColor DarkYellow
    }
  }
}

function Set-RegistryDefaultValue {
  param(
    [Parameter(Mandatory = $true)][string]$KeyPath,
    [Parameter(Mandatory = $true)][string]$Value
  )

  $relative = $KeyPath -replace '^HKCU:\\', ''
  $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($relative)
  try {
    $key.SetValue("", $Value, [Microsoft.Win32.RegistryValueKind]::String)
  } finally {
    $key.Dispose()
  }
}

function Get-RegistryDefaultValue {
  param([Parameter(Mandatory = $true)][string]$KeyPath)

  $relative = $KeyPath -replace '^HKCU:\\', ''
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($relative)
  if (-not $key) { return $null }
  try {
    return $key.GetValue("")
  } finally {
    $key.Dispose()
  }
}

function Install-AppPathShim {
  param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$TargetExe,
    [Parameter(Mandatory = $true)][string]$TargetDir
  )

  $keyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$Name"
  $defaultValue = Get-RegistryDefaultValue $keyPath
  $current = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
  New-Item -Path $keyPath -Force | Out-Null

  if ($defaultValue -and $defaultValue -ne $TargetExe -and -not $current.WimuxOriginalDefault) {
    New-ItemProperty -Path $keyPath -Name "WimuxOriginalDefault" -Value $defaultValue -PropertyType String -Force | Out-Null
  }
  if ($current.Path -and $current.Path -ne $TargetDir -and -not $current.WimuxOriginalPath) {
    New-ItemProperty -Path $keyPath -Name "WimuxOriginalPath" -Value $current.Path -PropertyType String -Force | Out-Null
  }

  Set-RegistryDefaultValue $keyPath $TargetExe
  New-ItemProperty -Path $keyPath -Name "Path" -Value $TargetDir -PropertyType String -Force | Out-Null
}

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
Get-Process wimux,wimux-web,wimux-cli -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "Extracting to $dest ..." -ForegroundColor DarkGray
Expand-Archive -Path $tmp -DestinationPath $dest -Force
Remove-Item -Force $tmp

if (-not (Test-Path (Join-Path $dest "wimux.exe"))) {
  throw "Install failed: wimux.exe not found in $dest"
}

# Older release bundles may not include shim exe names. Create them from the
# launcher so apps that call pwsh.exe/wt.exe can still be routed into Wimux.
if (-not (Test-Path (Join-Path $dest "pwsh.exe"))) {
  Copy-Item -Force (Join-Path $dest "wimux.exe") (Join-Path $dest "pwsh.exe")
}
if (-not (Test-Path (Join-Path $dest "wt.exe"))) {
  Copy-Item -Force (Join-Path $dest "wimux.exe") (Join-Path $dest "wt.exe")
}

# Put the install directory first on user PATH so shim names win over WindowsApps.
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$parts = @($userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $dest })
$newPath = (@($dest) + $parts) -join ";"
[Environment]::SetEnvironmentVariable("Path", $newPath, "User")
$env:Path = "$dest;$env:Path"
Write-Host "Added $dest first on your user PATH." -ForegroundColor Green

Install-WindowsTerminalProfile -InstallDir $dest
Install-WindowsTerminalSettingsProfile -InstallDir $dest
Install-AppPathShim "pwsh.exe" (Join-Path $dest "pwsh.exe") $dest
Install-AppPathShim "wt.exe" (Join-Path $dest "wt.exe") $dest
Write-Host "Configured pwsh.exe/wt.exe shims for desktop app integration." -ForegroundColor Green

$ver = & (Join-Path $dest "wimux.exe") version
Write-Host ""
Write-Host "Installed $ver" -ForegroundColor Green
Write-Host "Open a NEW terminal and run:  wimux" -ForegroundColor Green
Write-Host "Windows Terminal will also show a 'Wimux' profile after it reloads profiles." -ForegroundColor Green
