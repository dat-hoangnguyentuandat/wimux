# Restores normal wt.exe launch behavior after installing the Wimux terminal
# shim. It also removes legacy Wimux cmd.exe/pwsh.exe shims from earlier test
# installers if those entries point to Wimux.
#
# One-liner:
#   irm https://raw.githubusercontent.com/dat-hoangnguyentuandat/wimux/main/scripts/restore_wt.ps1 | iex
$ErrorActionPreference = "Stop"

$repo = if ($env:WIMUX_REPO) { $env:WIMUX_REPO } else { "dat-hoangnguyentuandat/wimux" }
$branch = if ($env:WIMUX_INSTALLER_BRANCH) { $env:WIMUX_INSTALLER_BRANCH } else { "main" }
$installer = "https://raw.githubusercontent.com/$repo/$branch/scripts/install.ps1"

$previous = $env:WIMUX_RESTORE_TERMINAL_SHIMS
try {
  $env:WIMUX_RESTORE_TERMINAL_SHIMS = "1"
  Invoke-RestMethod -Uri $installer | Invoke-Expression
} finally {
  if ($null -eq $previous) {
    Remove-Item Env:\WIMUX_RESTORE_TERMINAL_SHIMS -ErrorAction SilentlyContinue
  } else {
    $env:WIMUX_RESTORE_TERMINAL_SHIMS = $previous
  }
}
