# Runs backend (5201) and frontend dev server (5173) together.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$backend = Start-Process -PassThru -FilePath "dotnet" -ArgumentList "run","--project","server/Wimux.Web/Wimux.Web.csproj","--urls","http://localhost:5201" -WorkingDirectory $root
try {
  Push-Location "$root/web"
  npm run dev
} finally {
  Pop-Location
  if ($backend -and -not $backend.HasExited) { Stop-Process -Id $backend.Id -Force }
}
