using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Cmux.Web.Services.Browser;

/// <summary>
/// Controls a Chrome/Edge instance through its DevTools Protocol (CDP) HTTP endpoints.
/// Open/close/focus/list use the documented <c>/json/*</c> HTTP surface; reload uses a
/// short-lived per-target WebSocket to issue <c>Page.reload</c>.
///
/// The provider auto-launches an installed Chrome or Edge (Chrome preferred) with remote
/// debugging enabled, in a dedicated profile and pushed off-screen so cmux3 can show it
/// via CDP screencast. It never assumes a specific browser binary beyond the CDP contract.
/// </summary>
public sealed class ChromeCdpProvider : IBrowserProvider
{
    private readonly int _debugPort;
    private readonly string _devToolsBase;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    // The browser process cmux3 launched (if any), so we can fully quit it later.
    private Process? _browserProcess;

    public string Name => "Edge/Chrome (CDP)";

    public ChromeCdpProvider(int debugPort = 9222)
    {
        _debugPort = debugPort;
        _devToolsBase = $"http://127.0.0.1:{debugPort}";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    private async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync($"{_devToolsBase}/json/version", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task EnsureStartedAsync(CancellationToken ct = default)
    {
        if (await IsReachableAsync(ct)) return;

        await _launchLock.WaitAsync(ct);
        try
        {
            if (await IsReachableAsync(ct)) return;

            if (!TryLaunchBrowser())
                throw new BrowserUnavailableException(
                    "Could not find Chrome or Edge to launch. Install Google Chrome or " +
                    "Microsoft Edge, or start one manually with " +
                    $"--remote-debugging-port={_debugPort}.");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (await IsReachableAsync(ct)) return;
                await Task.Delay(300, ct);
            }

            throw new BrowserUnavailableException(
                $"Launched a browser but its debug port {_debugPort} never became reachable. " +
                "Another browser instance may already be running without remote debugging. " +
                "Close all Chrome/Edge windows and try again.");
        }
        finally { _launchLock.Release(); }
    }

    /// <summary>Locate an installed Chrome/Edge and start it with a dedicated debug profile.</summary>
    private bool TryLaunchBrowser()
    {
        var exe = FindBrowserExecutable();
        if (exe == null) return false;

        var profileDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cmux3", "browser-profile");
        Directory.CreateDirectory(profileDir);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = false,
            };
            psi.ArgumentList.Add($"--remote-debugging-port={_debugPort}");
            psi.ArgumentList.Add($"--user-data-dir={profileDir}");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--remote-allow-origins=*");
            // Run a REAL (non-headless) browser to avoid anti-bot challenges, but push the
            // window far off-screen so the user only sees it via CDP screencast.
            psi.ArgumentList.Add("--window-position=-32000,-32000");
            psi.ArgumentList.Add("--window-size=1280,800");
            psi.ArgumentList.Add("--disable-blink-features=AutomationControlled");
            // Off-screen windows are treated as occluded by Chromium, which pauses the
            // compositor and stops Page.startScreencast from emitting frames (the panel
            // would go blank). Disable occlusion/backgrounding throttling so the hidden
            // tab keeps painting and streaming.
            psi.ArgumentList.Add("--disable-features=Translate,AutomationControlled,CalculateNativeWinOcclusion");
            psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
            psi.ArgumentList.Add("--disable-renderer-backgrounding");
            psi.ArgumentList.Add("--disable-background-timer-throttling");
            psi.ArgumentList.Add("about:blank");
            _browserProcess = Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    private static string? FindBrowserExecutable()
    {
        // 1) App Paths in registry — Windows registers executables here regardless of
        //    install location, so we find Chrome/Edge even in non-standard paths.
        foreach (var key in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe",
        })
        {
            try
            {
                using var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (regKey?.GetValue("") is string path && File.Exists(path))
                    return path;
            }
            catch { /* registry access denied */ }
        }

        // 2) Standard installation folders as fallback.
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var exe in new[]
        {
            // Edge is preferred (always present on Windows, good anti-bot behavior).
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pfx86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(pfx86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe"),
        })
            if (File.Exists(exe)) return exe;

        return null;
    }

    public async Task<ProviderTab> OpenTabAsync(string url, CancellationToken ct = default)
    {
        var target = Uri.EscapeDataString(url);
        HttpResponseMessage resp;
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"{_devToolsBase}/json/new?{target}"))
            resp = await _http.SendAsync(put, ct);
        if (!resp.IsSuccessStatusCode)
            resp = await _http.GetAsync($"{_devToolsBase}/json/new?{target}", ct);
        resp.EnsureSuccessStatusCode();

        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return MapTab(node) ?? throw new InvalidOperationException("Failed to parse new tab response.");
    }

    public async Task CloseTabAsync(string tabId, CancellationToken ct = default)
    {
        try { await _http.GetAsync($"{_devToolsBase}/json/close/{tabId}", ct); }
        catch { /* best-effort; tab may already be gone */ }
    }

    public async Task FocusTabAsync(string tabId, CancellationToken ct = default)
    {
        await _http.GetAsync($"{_devToolsBase}/json/activate/{tabId}", ct);
    }

    public async Task ReloadTabAsync(string tabId, CancellationToken ct = default)
    {
        var tab = await GetTabAsync(tabId, ct);
        var wsUrl = tab?.DebuggerWebSocketUrl;
        if (string.IsNullOrEmpty(wsUrl)) return;

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "http://localhost");
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        var cmd = new JsonObject
        {
            ["id"] = 1,
            ["method"] = "Page.reload",
            ["params"] = new JsonObject { ["ignoreCache"] = false }
        };
        var bytes = Encoding.UTF8.GetBytes(cmd.ToJsonString());
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct); }
        catch { /* ignore */ }
    }

    public async Task<IReadOnlyList<ProviderTab>> ListTabsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{_devToolsBase}/json/list", ct);
        if (!resp.IsSuccessStatusCode) return Array.Empty<ProviderTab>();
        var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonArray;
        if (arr == null) return Array.Empty<ProviderTab>();
        return arr
            .Where(n => n?["type"]?.GetValue<string>() == "page")
            .Select(MapTab)
            .Where(t => t != null)
            .Select(t => t!)
            .ToList();
    }

    public async Task<ProviderTab?> GetTabAsync(string tabId, CancellationToken ct = default)
    {
        var tabs = await ListTabsAsync(ct);
        return tabs.FirstOrDefault(t => t.Id == tabId);
    }

    private static ProviderTab? MapTab(JsonNode? n)
    {
        if (n?["id"]?.GetValue<string>() is not { } id) return null;
        return new ProviderTab(
            Id: id,
            Title: n["title"]?.GetValue<string>() ?? "",
            Url: n["url"]?.GetValue<string>() ?? "",
            Favicon: n["faviconUrl"]?.GetValue<string>(),
            IsLoading: false)
        {
            DebuggerWebSocketUrl = n["webSocketDebuggerUrl"]?.GetValue<string>()
        };
    }

    public async Task QuitAsync(CancellationToken ct = default)
    {
        // Best-effort clean shutdown: ask Chromium to close via Browser.close,
        // then force-kill the launched process tree if it is still alive.
        try
        {
            if (await IsReachableAsync(ct))
            {
                using var resp = await _http.GetAsync($"{_devToolsBase}/json/version", ct);
                var versionJson = await resp.Content.ReadAsStringAsync(ct);
                var browserWs = JsonNode.Parse(versionJson)?["webSocketDebuggerUrl"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(browserWs))
                {
                    using var ws = new ClientWebSocket();
                    ws.Options.SetRequestHeader("Origin", "http://localhost");
                    try
                    {
                        await ws.ConnectAsync(new Uri(browserWs), ct);
                        var bytes = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}");
                        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
                        await Task.Delay(300, ct);
                    }
                    catch { /* ignore — may already be gone */ }
                }
            }
        }
        catch { /* ignore */ }

        var proc = _browserProcess;
        _browserProcess = null;
        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    try { await proc.WaitForExitAsync(ct); } catch { /* ignore */ }
                }
            }
            catch { /* already gone */ }
            try { proc.Dispose(); } catch { /* ignore */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await QuitAsync(CancellationToken.None);
        _http.Dispose();
        _launchLock.Dispose();
    }
}

/// <summary>Thrown when no CDP-capable browser can be reached.</summary>
public sealed class BrowserUnavailableException : Exception
{
    public BrowserUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}






