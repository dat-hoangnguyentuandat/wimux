# Builds a self-contained wimux release bundle for distribution.
#
#   ./scripts/release.ps1                  # -> dist/wimux-win-x64.zip
#   ./scripts/release.ps1 -Runtime win-x64
#
# The bundle contains wimux.exe (launcher), wimux-web.exe (host + SPA in
# wwwroot) and wimux-cli.exe (CLI). It needs no .NET runtime or Node on the target
# machine. Upload the resulting zip as a GitHub Release asset.
param(
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stage = Join-Path $root "dist/wimux-$Runtime"
$zip   = Join-Path $root "dist/wimux-$Runtime.zip"

Write-Host "Building SPA..." -ForegroundColor Cyan
Push-Location "$root/web"
try {
  if (-not (Test-Path "$root/web/node_modules")) { npm ci }
  npm run build
} finally { Pop-Location }

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$common = @(
  "-c", "Release",
  "-r", $Runtime,
  "--self-contained", "true",
  "-p:PublishSingleFile=false",
  "-o", $stage
)

Write-Host "Publishing web host..." -ForegroundColor Cyan
dotnet publish "$root/server/Wimux.Web/Wimux.Web.csproj" @common | Out-Host

Write-Host "Publishing CLI..." -ForegroundColor Cyan
dotnet publish "$root/server/Wimux.Cli/Wimux.Cli.csproj" @common | Out-Host

Write-Host "Publishing launcher..." -ForegroundColor Cyan
dotnet publish "$root/server/Wimux.Launcher/Wimux.Launcher.csproj" @common | Out-Host

if (-not (Test-Path (Join-Path $stage "wimux.exe"))) {
  throw "Build failed: wimux.exe missing from $stage"
}

Copy-Item -Force (Join-Path $stage "wimux.exe") (Join-Path $stage "pwsh.exe")
Copy-Item -Force (Join-Path $stage "wimux.exe") (Join-Path $stage "wt.exe")

Write-Host "Zipping bundle..." -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path "$stage/*" -DestinationPath $zip

$size = "{0:N1} MB" -f ((Get-Item $zip).Length / 1MB)
Write-Host ""
Write-Host "Created $zip ($size)" -ForegroundColor Green
Write-Host "Upload it to a GitHub Release, e.g.:" -ForegroundColor DarkGray
Write-Host "  gh release create v0.1.5 `"$zip`" --title v0.1.5 --notes `"wimux 0.1.5`"" -ForegroundColor DarkGray
