# Installs Wimux and opts in to the wt.exe terminal shim.
#
# One-liner:
#   irm https://raw.githubusercontent.com/dat-hoangnguyentuandat/wimux/main/scripts/install_wt.ps1 | iex
$ErrorActionPreference = "Stop"

$repo = if ($env:WIMUX_REPO) { $env:WIMUX_REPO } else { "dat-hoangnguyentuandat/wimux" }
$branch = if ($env:WIMUX_INSTALLER_BRANCH) { $env:WIMUX_INSTALLER_BRANCH } else { "main" }
$installer = "https://raw.githubusercontent.com/$repo/$branch/scripts/install.ps1"

$previous = $env:WIMUX_ENABLE_TERMINAL_SHIMS
try {
  $env:WIMUX_ENABLE_TERMINAL_SHIMS = "1"
  Invoke-RestMethod -Uri $installer | Invoke-Expression
} finally {
  if ($null -eq $previous) {
    Remove-Item Env:\WIMUX_ENABLE_TERMINAL_SHIMS -ErrorAction SilentlyContinue
  } else {
    $env:WIMUX_ENABLE_TERMINAL_SHIMS = $previous
  }
}
