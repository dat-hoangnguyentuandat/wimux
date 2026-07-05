using System.Net.Http;
using System.Text.Json;
using System.IO.Compression;
using System.Diagnostics;

namespace Wimux.Launcher;

/// <summary>
/// Best-effort update check and self-update support against GitHub releases.
/// Network failures are converted to messages so the menu always works offline.
/// </summary>
internal static class UpdateChecker
{
    // Override with WIMUX_REPO=owner/name if the project moves.
    private static string Repo =>
        Environment.GetEnvironmentVariable("WIMUX_REPO")
        ?? "dat-hoangnguyentuandat/wimux";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("wimux-launcher");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private const string Runtime = "win-x64";
    private const string AssetName = $"wimux-{Runtime}.zip";

    internal static string? LatestVersion { get; private set; }

    /// <summary>Returns the latest tag if it is newer than the current build.</summary>
    internal static string? CheckForUpdate()
    {
        var release = GetLatestRelease();
        if (release == null) return null;

        LatestVersion = release.Version;
        return IsNewer(release.Version, Program.CurrentVersion) ? release.Version : null;
    }

    internal static UpdateInstallResult InstallLatest()
    {
        try
        {
            var release = GetLatestRelease();
            if (release == null)
                return UpdateInstallResult.Fail("Could not query the latest GitHub release.");

            LatestVersion = release.Version;
            if (!IsNewer(release.Version, Program.CurrentVersion))
                return UpdateInstallResult.NotNeeded(release.Version);

            if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
                return UpdateInstallResult.Fail($"Release v{release.Version} has no asset named {AssetName}.");

            var targetDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!File.Exists(Path.Combine(targetDir, "wimux.exe")))
                return UpdateInstallResult.Fail("Self-update only works from a published wimux install folder.");

            var updateRoot = Path.Combine(Path.GetTempPath(), "wimux-update-" + Guid.NewGuid().ToString("N"));
            var zipPath = Path.Combine(updateRoot, AssetName);
            var stagingDir = Path.Combine(updateRoot, "staging");
            Directory.CreateDirectory(updateRoot);

            DownloadFile(release.AssetDownloadUrl, zipPath);
            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            if (!File.Exists(Path.Combine(stagingDir, "wimux.exe")))
                return UpdateInstallResult.Fail($"Downloaded asset does not contain wimux.exe: {AssetName}.");

            var scriptPath = WriteUpdaterScript(updateRoot);
            var currentPid = Environment.ProcessId;
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal,
                Arguments =
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                    $"-TargetDir \"{targetDir}\" -StagingDir \"{stagingDir}\" -LauncherPid {currentPid} -Version \"{release.Version}\"",
            };
            Process.Start(psi);
            return UpdateInstallResult.Scheduled(release.Version);
        }
        catch (Exception ex)
        {
            return UpdateInstallResult.Fail(ex.Message);
        }
    }

    private static ReleaseInfo? GetLatestRelease()
    {
        try
        {
            var url = $"https://api.github.com/repos/{Repo}/releases/latest";
            using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag))
                return null;

            var latest = (tag.GetString() ?? "").TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(latest))
                return null;

            string? assetUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    assetUrl = asset.TryGetProperty("browser_download_url", out var dl) ? dl.GetString() : null;
                    break;
                }
            }

            return new ReleaseInfo(latest, assetUrl);
        }
        catch
        {
            return null;
        }
    }

    private static void DownloadFile(string url, string path)
    {
        using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        using var input = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var output = File.Create(path);
        input.CopyTo(output);
    }

    private static string WriteUpdaterScript(string updateRoot)
    {
        var scriptPath = Path.Combine(updateRoot, "apply-update.ps1");
        File.WriteAllText(scriptPath, """
param(
  [Parameter(Mandatory=$true)][string]$TargetDir,
  [Parameter(Mandatory=$true)][string]$StagingDir,
  [Parameter(Mandatory=$true)][int]$LauncherPid,
  [Parameter(Mandatory=$true)][string]$Version
)
$ErrorActionPreference = "Stop"
Write-Host "Applying wimux update v$Version..." -ForegroundColor Cyan
try { Wait-Process -Id $LauncherPid -Timeout 45 -ErrorAction SilentlyContinue } catch {}

$targetFull = [System.IO.Path]::GetFullPath($TargetDir).TrimEnd('\','/')
$parent = Split-Path -Parent $targetFull
$backup = Join-Path $parent ("wimux.backup." + [guid]::NewGuid().ToString("N"))

Get-CimInstance Win32_Process |
  Where-Object {
    $_.ExecutablePath -and
    ([System.IO.Path]::GetFullPath($_.ExecutablePath).StartsWith($targetFull, [System.StringComparison]::OrdinalIgnoreCase))
  } |
  ForEach-Object {
    try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
  }
Start-Sleep -Milliseconds 500

if (Test-Path $backup) { Remove-Item -Recurse -Force $backup }
if (Test-Path $targetFull) { Move-Item -LiteralPath $targetFull -Destination $backup -Force }
New-Item -ItemType Directory -Force -Path $targetFull | Out-Null
Copy-Item -Path (Join-Path $StagingDir '*') -Destination $targetFull -Recurse -Force

if (-not (Test-Path (Join-Path $targetFull 'wimux.exe'))) {
  if (Test-Path $targetFull) { Remove-Item -Recurse -Force $targetFull }
  if (Test-Path $backup) { Move-Item -LiteralPath $backup -Destination $targetFull -Force }
  throw "Update failed: wimux.exe missing after copy."
}

if (Test-Path $backup) { Remove-Item -Recurse -Force $backup -ErrorAction SilentlyContinue }
Write-Host "Updated wimux to v$Version." -ForegroundColor Green
Start-Process -FilePath (Join-Path $targetFull 'wimux.exe')
""");
        return scriptPath;
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            return l > c;
        return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ReleaseInfo(string Version, string? AssetDownloadUrl);
}

internal sealed record UpdateInstallResult(bool Started, bool UpToDate, string? Version, string? Error)
{
    internal static UpdateInstallResult Scheduled(string version) => new(true, false, version, null);
    internal static UpdateInstallResult NotNeeded(string version) => new(false, true, version, null);
    internal static UpdateInstallResult Fail(string error) => new(false, false, null, error);
}
