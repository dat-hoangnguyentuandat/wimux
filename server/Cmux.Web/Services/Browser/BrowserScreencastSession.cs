using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cmux.Web.Services.Browser;

/// <summary>
/// Bridges a single CDP target (tab) to a cmux3 client: streams the tab as JPEG
/// frames via <c>Page.startScreencast</c> and forwards mouse/keyboard/scroll input
/// back through <c>Input.dispatch*</c>. This is remote control of a real headless
/// tab — no CSP/X-Frame bypass is involved.
/// </summary>
public sealed class BrowserScreencastSession : IAsyncDisposable
{
    private readonly ClientWebSocket _cdp = new();
    private int _cmdId;
    // In-flight CDP request id -> completion (filled by HandleMessage when the
    // matching response arrives; consumed by GetNavigationHistoryAsync).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    /// <summary>Raised for each screencast frame (base64 JPEG) ready to send to the client.</summary>
    public event Func<string, Task>? FrameReceived;

    /// <summary>Raised when the tab's title or URL changes (title, url).</summary>
    public event Func<string, string, Task>? MetaReceived;

    // Reserved CDP command id used to poll the live document title/URL.
    private const int MetaEvalId = 1_000_000;

    /// <summary>Connect to the tab's debugger socket and start screencasting.</summary>
    public async Task StartAsync(string debuggerWsUrl, CancellationToken ct)
    {
        _cdp.Options.SetRequestHeader("Origin", "http://localhost");
        await _cdp.ConnectAsync(new Uri(debuggerWsUrl), ct);

        _ = Task.Run(() => ReceiveLoop(ct), ct);

        await SendAsync("Page.enable", null, ct);
        await SendAsync("Runtime.enable", null, ct);
        // Non-headless Chrome only renders/screencasts the foreground tab. Bring
        // this target to the front so background tabs still produce frames.
        await SendAsync("Page.bringToFront", null, ct);
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

    public Task DispatchKeyAsync(string type, string key, string code, int keyCode, string? text, CancellationToken ct)
    {
        var p = new JsonObject
        {
            ["type"] = type,
            ["key"] = key,
            ["code"] = code,
            ["windowsVirtualKeyCode"] = keyCode,
            ["nativeVirtualKeyCode"] = keyCode,
        };
        if (!string.IsNullOrEmpty(text))
        {
            p["text"] = text;
            if (type == "keyDown") p["type"] = "keyDown";
        }
        return SendAsync("Input.dispatchKeyEvent", p, ct);
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
        var id = Interlocked.Increment(ref _cmdId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var cmd = new JsonObject { ["id"] = id, ["method"] = "Page.getNavigationHistory", ["params"] = new JsonObject() };
            await SendRawAsync(cmd, ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            JsonNode? result = null;
            using (linked.Token.Register(() => tcs.TrySetResult(null)))
                result = await tcs.Task;
            var arr = result?["entries"] as JsonArray;
            var current = result?["currentIndex"]?.GetValue<int>();
            if (arr == null || current == null) return null;
            var ids = new List<int>();
            foreach (var e in arr)
                if (e?["id"]?.GetValue<int>() is int eid) ids.Add(eid);
            return (current.Value, ids);
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

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cdp.State == WebSocketState.Open)
            {
                await SendAsync("Page.stopScreencast", null, CancellationToken.None);
                await _cdp.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        }
        catch { /* ignore */ }
        _cdp.Dispose();
    }
}











