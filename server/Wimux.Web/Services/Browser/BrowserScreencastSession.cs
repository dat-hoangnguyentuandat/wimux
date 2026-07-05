using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wimux.Web.Services.Browser;

/// <summary>
/// Bridges a single CDP target (tab) to a wimux client: streams the tab as JPEG
/// frames via <c>Page.startScreencast</c> and forwards mouse/keyboard/scroll input
/// back through <c>Input.dispatch*</c>. This is remote control of a real headless
/// tab — no CSP/X-Frame bypass is involved.
/// </summary>
public sealed class BrowserScreencastSession : IAsyncDisposable
{
    private readonly ClientWebSocket _cdp = new();
    private readonly ClientWebSocket _browserCdp = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<string, byte> _knownPageTargets = new();
    private readonly Func<CancellationToken, Task> _suppressBrowserUi;
    private string? _tabId;
    private Uri? _devToolsListUri;
    private int _cmdId;
    private int _browserCmdId;
    // In-flight CDP request id -> completion (filled by HandleMessage when the
    // matching response arrives; consumed by GetNavigationHistoryAsync).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    /// <summary>Raised for each screencast frame (base64 JPEG) ready to send to the client.</summary>
    public event Func<string, Task>? FrameReceived;

    /// <summary>Raised when the tab's title or URL changes (title, url).</summary>
    public event Func<string, string, Task>? MetaReceived;

    /// <summary>Raised when the page tries to open a new browser target.</summary>
    public event Func<string, string, bool, Task>? PopupTargetCreated;

    /// <summary>Raised when a browser target disappears.</summary>
    public event Func<string, Task>? PopupTargetClosed;

    /// <summary>Raised when text is copied out of the remote page.</summary>
    public event Func<string, Task>? ClipboardTextReceived;

    // Reserved CDP command id used to poll the live document title/URL.
    private const int MetaEvalId = 1_000_000;

    public BrowserScreencastSession(Func<CancellationToken, Task>? suppressBrowserUi = null)
    {
        _suppressBrowserUi = suppressBrowserUi ?? (_ => Task.CompletedTask);
    }

    /// <summary>Connect to the tab's debugger socket and start screencasting.</summary>
    public async Task StartAsync(string debuggerWsUrl, string tabId, int initialWidth, int initialHeight, double initialDpr, CancellationToken ct)
    {
        _tabId = tabId;
        _cdp.Options.SetRequestHeader("Origin", "http://localhost");
        await _cdp.ConnectAsync(new Uri(debuggerWsUrl), ct);

        _ = Task.Run(() => ReceiveLoop(ct), ct);
        ConfigureDevToolsHttp(debuggerWsUrl);
        await SnapshotPageTargetsAsync(ct);

        await SendAsync("Page.enable", null, ct);
        await SendAsync("Runtime.enable", null, ct);
        await StartBrowserTargetMonitorAsync(debuggerWsUrl, ct);
        await SetViewportAsync(
            initialWidth > 0 ? initialWidth : 1280,
            initialHeight > 0 ? initialHeight : 800,
            initialDpr > 0 ? initialDpr : 1,
            ct);
        // Non-headless Chrome only renders/screencasts the foreground tab. Bring
        // this target to the front so background tabs still produce frames.
        await SendAsync("Page.bringToFront", null, ct);
        await SuppressBrowserUiAsync(ct);
        await SendAsync("Page.startScreencast", new JsonObject
        {
            ["format"] = "jpeg",
            ["quality"] = 70,
            ["maxWidth"] = 1600,
            ["maxHeight"] = 1000,
            ["everyNthFrame"] = 1,
        }, ct);
        // Report the initial title/URL so the tab label is correct on connect.
        await RequestMetaAsync(ct);
    }

    private void ConfigureDevToolsHttp(string targetDebuggerWsUrl)
    {
        try
        {
            var targetUri = new Uri(targetDebuggerWsUrl);
            var scheme = targetUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            _devToolsListUri = new Uri($"{scheme}://{targetUri.Host}:{targetUri.Port}/json/list");
        }
        catch { _devToolsListUri = null; }
    }

    private async Task StartBrowserTargetMonitorAsync(string targetDebuggerWsUrl, CancellationToken ct)
    {
        var browserWsUrl = await ResolveBrowserDebuggerUrlAsync(targetDebuggerWsUrl, ct);
        if (string.IsNullOrWhiteSpace(browserWsUrl)) return;

        _browserCdp.Options.SetRequestHeader("Origin", "http://localhost");
        await _browserCdp.ConnectAsync(new Uri(browserWsUrl), ct);
        _ = Task.Run(() => BrowserReceiveLoop(ct), ct);

        await SendBrowserAsync("Target.setDiscoverTargets", new JsonObject { ["discover"] = true }, ct);
    }

    private async Task<string?> ResolveBrowserDebuggerUrlAsync(string targetDebuggerWsUrl, CancellationToken ct)
    {
        try
        {
            var targetUri = new Uri(targetDebuggerWsUrl);
            var scheme = targetUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
            var versionUri = new Uri($"{scheme}://{targetUri.Host}:{targetUri.Port}/json/version");
            var json = await _http.GetStringAsync(versionUri, ct);
            return JsonNode.Parse(json)?["webSocketDebuggerUrl"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private async Task SnapshotPageTargetsAsync(CancellationToken ct)
    {
        foreach (var (id, _) in await ListPageTargetsAsync(ct))
            _knownPageTargets.TryAdd(id, 0);
        if (!string.IsNullOrEmpty(_tabId))
            _knownPageTargets.TryAdd(_tabId, 0);
    }

    private async Task<List<(string id, string url)>> ListPageTargetsAsync(CancellationToken ct)
    {
        var rows = new List<(string id, string url)>();
        if (_devToolsListUri == null) return rows;
        try
        {
            var json = await _http.GetStringAsync(_devToolsListUri, ct);
            if (JsonNode.Parse(json) is not JsonArray arr) return rows;
            foreach (var node in arr)
            {
                if (node?["type"]?.GetValue<string>() != "page") continue;
                var id = node["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                rows.Add((id, node["url"]?.GetValue<string>() ?? ""));
            }
        }
        catch { /* best-effort popup discovery */ }
        return rows;
    }

    /// <summary>Ask the page for its current document.title + location.href.</summary>
    private Task RequestMetaAsync(CancellationToken ct)
    {
        var cmd = new JsonObject
        {
            ["id"] = MetaEvalId,
            ["method"] = "Runtime.evaluate",
            ["params"] = new JsonObject
            {
                ["expression"] = "JSON.stringify({title:document.title,url:location.href})",
                ["returnByValue"] = true,
            },
        };
        return SendRawAsync(cmd, ct);
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var sb = new StringBuilder();
        try
        {
            while (_cdp.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _cdp.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                await HandleMessage(sb.ToString(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task HandleMessage(string raw, CancellationToken ct)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch { return; }
        var method = node?["method"]?.GetValue<string>();

        // Screencast frame: ack and forward.
        if (node != null && method == "Page.screencastFrame")
        {
            var data = node["params"]?["data"]?.GetValue<string>();
            var sessionId = node["params"]?["sessionId"]?.GetValue<int>();
            if (sessionId is int sid)
                await SendAsync("Page.screencastFrameAck", new JsonObject { ["sessionId"] = sid }, ct);
            if (!string.IsNullOrEmpty(data) && FrameReceived != null)
                await FrameReceived(data);
            return;
        }

        // Page navigation / load: re-poll the current title/URL.
        if (method == "Page.frameNavigated" ||
            method == "Page.navigatedWithinDocument" ||
            method == "Page.loadEventFired" ||
            method == "Page.domContentEventFired")
        {
            await RequestMetaAsync(ct);
            return;
        }

        // CDP response to a tracked command (e.g. Page.getNavigationHistory).
        if (method == null && node?["id"]?.GetValue<int>() is int respId && respId != MetaEvalId)
        {
            if (_pending.TryRemove(respId, out var tcs))
                tcs.TrySetResult(node["error"] != null ? null : node["result"]?.DeepClone());
            return;
        }

        // Response to our Runtime.evaluate poll.
        if (method == null &&
            node?["id"]?.GetValue<int>() == MetaEvalId &&
            MetaReceived != null)
        {
            var value = node["result"]?["result"]?["value"]?.GetValue<string>();
            if (string.IsNullOrEmpty(value)) return;
            string title = "", url = "";
            try
            {
                var parsed = JsonNode.Parse(value) as JsonObject;
                title = parsed?["title"]?.GetValue<string>() ?? "";
                url = parsed?["url"]?.GetValue<string>() ?? "";
            }
            catch { return; }
            if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(url))
                await MetaReceived(title, url);
        }
    }

    private async Task BrowserReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var sb = new StringBuilder();
        try
        {
            while (_browserCdp.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult result;
                do
                {
                    result = await _browserCdp.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                } while (!result.EndOfMessage);

                await HandleBrowserMessage(sb.ToString(), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private async Task HandleBrowserMessage(string raw, CancellationToken ct)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(raw); }
        catch { return; }
        var method = node?["method"]?.GetValue<string>();
        if (method == "Target.targetDestroyed")
        {
            var closedTargetId = node?["params"]?["targetId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(closedTargetId))
            {
                _knownPageTargets.TryRemove(closedTargetId, out _);
                if (PopupTargetClosed != null)
                    await PopupTargetClosed(closedTargetId);
            }
            return;
        }
        if (method != "Target.targetCreated" && method != "Target.targetInfoChanged") return;

        var info = node?["params"]?["targetInfo"];
        var targetId = info?["targetId"]?.GetValue<string>();
        var type = info?["type"]?.GetValue<string>();
        var openerId = info?["openerId"]?.GetValue<string>();
        var url = info?["url"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrEmpty(targetId) &&
            targetId != _tabId &&
            string.Equals(type, "page", StringComparison.OrdinalIgnoreCase) &&
            openerId == _tabId)
        {
            await NotifyNewTargetIfNeeded(targetId, url);
        }
    }

    private async Task NotifyNewTargetIfNeeded(string targetId, string url)
    {
        if (string.IsNullOrEmpty(targetId) || targetId == _tabId) return;
        if (!_knownPageTargets.TryAdd(targetId, 0)) return;
        if (PopupTargetCreated != null)
            await PopupTargetCreated(targetId, url, true);
    }

    // ── Input forwarding ────────────────────────────────────────────────

    public Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["button"] = button,
            ["clickCount"] = clickCount,
        };
        if (type == "mouseWheel")
        {
            p["deltaX"] = deltaX;
            p["deltaY"] = deltaY;
        }
        return SendAsync("Input.dispatchMouseEvent", p, ct);
    }

    public Task DispatchKeyAsync(
        string type,
        string key,
        string code,
        int keyCode,
        string? text,
        bool altKey,
        bool ctrlKey,
        bool metaKey,
        bool shiftKey,
        CancellationToken ct)
    {
        var modifiers = 0;
        if (altKey) modifiers |= 1;
        if (ctrlKey) modifiers |= 2;
        if (metaKey) modifiers |= 4;
        if (shiftKey) modifiers |= 8;

        // Browser accelerators such as Ctrl+C / Ctrl+V are non-text key events
        // in CDP. Sending them as text input prevents Chromium from treating
        // them like native shortcuts.
        var cdpType = type == "keyDown" && string.IsNullOrEmpty(text)
            ? "rawKeyDown"
            : type;
        var p = new JsonObject
        {
            ["type"] = cdpType,
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = keyCode,
            ["nativeVirtualKeyCode"] = keyCode,
            ["modifiers"] = modifiers,
        };
        if (!string.IsNullOrEmpty(text))
        {
            p["text"] = text;
            if (type == "keyDown") p["type"] = "keyDown";
        }
        return SendAsync("Input.dispatchKeyEvent", p, ct);
    }

    public async Task FocusAsync(CancellationToken ct)
    {
        await SendAsync("Page.bringToFront", null, ct);
        await SuppressBrowserUiAsync(ct);
    }

    private async Task SuppressBrowserUiAsync(CancellationToken ct)
    {
        try { await _suppressBrowserUi(ct); }
        catch { /* UI suppression is best-effort; streaming must continue. */ }
    }

    public async Task CopySelectionAsync(CancellationToken ct)
    {
        var result = await EvaluateAsync("""
(() => {
  const active = document.activeElement;
  if (active && (
      active instanceof HTMLInputElement ||
      active instanceof HTMLTextAreaElement)) {
    const start = active.selectionStart ?? 0;
    const end = active.selectionEnd ?? 0;
    if (end > start) return active.value.substring(start, end);
  }
  return globalThis.getSelection ? globalThis.getSelection().toString() : "";
})()
""", ct);
        var text = result?["result"]?["value"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrEmpty(text) && ClipboardTextReceived != null)
            await ClipboardTextReceived(text);
    }

    public Task PasteTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
        return SendAsync("Input.insertText", new JsonObject { ["text"] = text }, ct);
    }

    public Task NavigateAsync(string url, CancellationToken ct)
        => SendAsync("Page.navigate", new JsonObject { ["url"] = url }, ct);

    public Task ReloadAsync(CancellationToken ct)
        => SendAsync("Page.reload", new JsonObject { ["ignoreCache"] = false }, ct);

    /// <summary>Go back/forward in the tab history via CDP.</summary>
    public async Task HistoryGoAsync(int delta, CancellationToken ct)
    {
        var history = await GetNavigationHistoryAsync(ct);
        if (history == null) return;
        var (currentIndex, entries) = history.Value;
        var target = currentIndex + delta;
        if (target < 0 || target >= entries.Count) return;
        var entryId = entries[target];
        await SendAsync("Page.navigateToHistoryEntry", new JsonObject { ["entryId"] = entryId }, ct);
    }

    private async Task<(int currentIndex, List<int> entryIds)?> GetNavigationHistoryAsync(CancellationToken ct)
    {
        try
        {
            var result = await SendCommandForResultAsync("Page.getNavigationHistory", new JsonObject(), ct);
            var arr = result?["entries"] as JsonArray;
            var current = result?["currentIndex"]?.GetValue<int>();
            if (arr == null || current == null) return null;
            var ids = new List<int>();
            foreach (var e in arr)
                if (e?["id"]?.GetValue<int>() is int eid) ids.Add(eid);
            return (current.Value, ids);
        }
        catch { return null; }
    }

    private Task<JsonNode?> EvaluateAsync(string expression, CancellationToken ct)
        => SendCommandForResultAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
        }, ct);

    private async Task<JsonNode?> SendCommandForResultAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _cmdId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var cmd = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params ?? new JsonObject(),
            };
            await SendRawAsync(cmd, ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using (linked.Token.Register(() => tcs.TrySetResult(null)))
                return await tcs.Task;
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task SetViewportAsync(int width, int height, double dpr, CancellationToken ct)
        => SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
        {
            ["width"] = width,
            ["height"] = height,
            ["deviceScaleFactor"] = dpr,
            ["mobile"] = false,
        }, ct);

    private async Task SendAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (_cdp.State != WebSocketState.Open) return;
        var id = Interlocked.Increment(ref _cmdId);
        var cmd = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
        };
        await SendRawAsync(cmd, ct);
    }

    private async Task SendRawAsync(JsonNode cmd, CancellationToken ct)
    {
        if (_cdp.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(cmd.ToJsonString());
        await _cdp.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private async Task SendBrowserAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (_browserCdp.State != WebSocketState.Open) return;
        var id = Interlocked.Increment(ref _browserCmdId);
        var cmd = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
        };
        var bytes = Encoding.UTF8.GetBytes(cmd.ToJsonString());
        await _browserCdp.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cdp.State == WebSocketState.Open)
            {
                await SendAsync("Page.stopScreencast", null, CancellationToken.None);
                await _cdp.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            if (_browserCdp.State == WebSocketState.Open)
                await _browserCdp.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch { /* ignore */ }
        _cdp.Dispose();
        _browserCdp.Dispose();
        _http.Dispose();
    }
}











