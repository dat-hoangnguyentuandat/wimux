# Builds the wimux launcher (plus the web host + CLI) and puts "wimux" on PATH.
#
#   ./install.ps1                         # build Release, install for current user
#   ./install.ps1 -NoBuild                # skip build, just (re)install PATH entry
#   ./install.ps1 -InstallTerminalShims    # also put wimux first on PATH for pwsh.exe/wt.exe
#   ./install.ps1 -InstallTerminalAppPaths # override Windows App Paths for pwsh.exe/wt.exe
#   ./install.ps1 -RestoreTerminalAppPaths # restore backed-up App Paths
#
# After install, open a new terminal and run:  wimux
param(
  [switch]$NoBuild,
  [switch]$InstallTerminalShims,
  [switch]$InstallTerminalAppPaths,
  [switch]$RestoreTerminalAppPaths
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

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

function Restore-AppPathShim {
  param([Parameter(Mandatory = $true)][string]$Name)

  $keyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\$Name"
  $current = Get-ItemProperty -Path $keyPath -ErrorAction SilentlyContinue
  if (-not $current) { return }

  if ($current.WimuxOriginalDefault) {
    Set-RegistryDefaultValue $keyPath $current.WimuxOriginalDefault
    Remove-ItemProperty -Path $keyPath -Name "WimuxOriginalDefault" -ErrorAction SilentlyContinue
  }
  if ($current.WimuxOriginalPath) {
    New-ItemProperty -Path $keyPath -Name "Path" -Value $current.WimuxOriginalPath -PropertyType String -Force | Out-Null
    Remove-ItemProperty -Path $keyPath -Name "WimuxOriginalPath" -ErrorAction SilentlyContinue
  }
}

if (-not $NoBuild) {
  Write-Host "Building wimux (launcher + web host + CLI)..." -ForegroundColor Cyan
  $dist = Join-Path $root "dist"
  if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
  New-Item -ItemType Directory -Force -Path $dist | Out-Null

  # Build the SPA so the web host can serve the UI.
  Push-Location "$root/web"
  try {
    if (-not (Test-Path "$root/web/node_modules")) { npm install }
    npm run build
  } finally { Pop-Location }

  dotnet publish "$root/server/Wimux.Launcher/Wimux.Launcher.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "$dist" | Out-Host
  dotnet publish "$root/server/Wimux.Web/Wimux.Web.csproj"           -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "$dist" | Out-Host
  dotnet publish "$root/server/Wimux.Cli/Wimux.Cli.csproj"           -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "$dist" | Out-Host

  Copy-Item -Force (Join-Path $dist "wimux.exe") (Join-Path $dist "pwsh.exe")
  Copy-Item -Force (Join-Path $dist "wimux.exe") (Join-Path $dist "wt.exe")
}

$dist = Join-Path $root "dist"
if (-not (Test-Path (Join-Path $dist "wimux.exe"))) {
  throw "wimux.exe not found in $dist. Run without -NoBuild first."
}

# Add the dist folder to the user PATH if it is not already there.
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if (($userPath -split ";") -notcontains $dist) {
  $newPath = if ([string]::IsNullOrEmpty($userPath)) { $dist } else { "$userPath;$dist" }
  [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
  Write-Host "Added $dist to your user PATH." -ForegroundColor Green
} else {
  Write-Host "$dist already on PATH." -ForegroundColor DarkGray
}

if (-not (Test-Path (Join-Path $dist "pwsh.exe"))) {
  Copy-Item -Force (Join-Path $dist "wimux.exe") (Join-Path $dist "pwsh.exe")
  Copy-Item -Force (Join-Path $dist "wimux.exe") (Join-Path $dist "wt.exe")
}

if ($InstallTerminalShims) {
  $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
  $parts = @($userPath -split ";" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $dist })
  $newPath = (@($dist) + $parts) -join ";"
  [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
  Write-Host "Added wimux dist first on PATH for terminal shims: $dist" -ForegroundColor Green
  Write-Host "Apps that launch pwsh.exe or wt.exe from PATH can now open in wimux." -ForegroundColor Green
} else {
  Write-Host "Terminal shims are available as pwsh.exe/wt.exe in: $dist" -ForegroundColor DarkGray
  Write-Host "Run ./install.ps1 -NoBuild -InstallTerminalShims to make pwsh.exe/wt.exe open in wimux." -ForegroundColor DarkGray
}

if ($RestoreTerminalAppPaths) {
  Restore-AppPathShim "pwsh.exe"
  Restore-AppPathShim "wt.exe"
  Write-Host "Restored original App Paths for pwsh.exe/wt.exe." -ForegroundColor Green
}

if ($InstallTerminalAppPaths) {
  Install-AppPathShim "pwsh.exe" (Join-Path $dist "pwsh.exe") $dist
  Install-AppPathShim "wt.exe" (Join-Path $dist "wt.exe") $dist
  Write-Host "Overrode HKCU App Paths for pwsh.exe/wt.exe to wimux shims." -ForegroundColor Green
  Write-Host "Restart the desktop app so it reads the updated launch rules." -ForegroundColor Green
}

Install-WindowsTerminalProfile -InstallDir $dist
Install-WindowsTerminalSettingsProfile -InstallDir $dist

Write-Host ""
Write-Host "Done. Open a NEW terminal and run:  wimux" -ForegroundColor Green
Write-Host "Windows Terminal will also show a 'Wimux' profile after it reloads profiles." -ForegroundColor Green
