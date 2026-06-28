using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cmux.Web.Services.Browser;

namespace Cmux.Web.Services;

/// <summary>
/// Wires browser-manager commands from the frontend into the local Chrome instance.
/// The frontend never talks CDP directly; it sends compact JSON messages over this
/// WebSocket or hits the REST shortcuts below.
/// </summary>
public static class BrowserEndpointExtensions
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Compact WS message format:
    //   cmd <tabId|url>  →  command (one char) + payload
    //   c <url>          →  open tab
    //   f <tabId>        →  focus tab
    //   x <tabId>        →  close tab
    //   r <tabId>        →  reload tab
    //   s                →  sync / list tabs
    //   a                →  get active tab
    // Response is always a JSON string prefixed with a type char:
    //   O <json>         →  single BrowserTabState (open / focus / reload)
    //   L <json array>   →  list of BrowserTabState
    //   E <msg>          →  error string

    public static void MapBrowserEndpoints(this WebApplication app)
    {
        var manager = app.Services.GetRequiredService<BrowserManager>();

        // ── REST shortcuts (one-shot, no persistent connection) ─────────

        app.MapGet("/api/browser/tabs", (BrowserManager m) =>
            Results.Json(m.GetTabs(), JsonOpts));

        // Probe a URL's real response headers to decide if it can be iframed.
        // Checks X-Frame-Options (DENY/SAMEORIGIN) and CSP frame-ancestors.
        app.MapGet("/api/browser/can-embed", async (IHttpClientFactory factory, string url, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Results.Json(new { canEmbed = false, reason = "invalid-url" }, JsonOpts);

            var client = factory.CreateClient("frame-proxy");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                string? xfo = resp.Headers.TryGetValues("X-Frame-Options", out var xs) ? string.Join(",", xs) : null;
                string? csp = resp.Headers.TryGetValues("Content-Security-Policy", out var cs) ? string.Join(";", cs)
                            : resp.Content.Headers.TryGetValues("Content-Security-Policy", out var cc) ? string.Join(";", cc) : null;

                var blockedByXfo = xfo != null &&
                    (xfo.Contains("DENY", StringComparison.OrdinalIgnoreCase) ||
                     xfo.Contains("SAMEORIGIN", StringComparison.OrdinalIgnoreCase));

                var blockedByCsp = false;
                if (csp != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        csp, @"frame-ancestors([^;]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        var directive = match.Groups[1].Value.Trim();
                        // 'none' always blocks. A wildcard '*' allows. Otherwise the directive
                        // lists specific origins (not our app), so we treat it as blocked.
                        if (directive.Contains("'none'", StringComparison.OrdinalIgnoreCase))
                            blockedByCsp = true;
                        else if (directive.Length > 0 && !directive.Contains('*'))
                            blockedByCsp = true;
                    }
                }

                var canEmbed = !blockedByXfo && !blockedByCsp;
                return Results.Json(new
                {
                    canEmbed,
                    reason = blockedByXfo ? "x-frame-options" : blockedByCsp ? "csp-frame-ancestors" : "ok",
                    xFrameOptions = xfo,
                }, JsonOpts);
            }
            catch (Exception ex)
            {
                // Network failure: let the client try (may be localhost / offline asset).
                return Results.Json(new { canEmbed = true, reason = "probe-failed", error = ex.Message }, JsonOpts);
            }
        });

        app.MapGet("/api/browser/active", (BrowserManager m) =>
        {
            var tab = m.GetActiveTab();
            return tab == null ? Results.Json(new { }) : Results.Json(tab, JsonOpts);
        });

        app.MapPost("/api/browser/open", async (BrowserManager m, OpenTabReq req, CancellationToken ct) =>
        {
            try
            {
                var tab = await m.OpenTabAsync(req.Url ?? "", ct);
                return Results.Json(tab, JsonOpts);
            }
            catch (BrowserUnavailableException ex)
            {
                return Results.Json(new { error = ex.Message }, JsonOpts);
            }
        });

        app.MapPost("/api/browser/focus/{tabId}", (BrowserManager m, string tabId, CancellationToken ct) =>
        {
            _ = m.FocusTabAsync(tabId, ct);
            return Results.Ok();
        });

        app.MapPost("/api/browser/close/{tabId}", (BrowserManager m, string tabId, CancellationToken ct) =>
        {
            _ = m.CloseTabAsync(tabId, ct);
            return Results.Ok();
        });

        // Close the live Chrome tab bound to a cmux3 client (pane) tab id.
        // The frontend calls this on tab close / pane close so the real
        // browser tab on the host is shut down — not left running off-screen.
        app.MapDelete("/api/browser/client-tab/{clientTabId}", async (BrowserManager m, string clientTabId, CancellationToken ct) =>
        {
            await m.CloseClientTabAsync(clientTabId, ct);
            return Results.Ok();
        });

        app.MapPost("/api/browser/reload/{tabId}", (BrowserManager m, string tabId, CancellationToken ct) =>
        {
            _ = m.ReloadTabAsync(tabId, ct);
            return Results.Ok();
        });

        app.MapPost("/api/browser/sync", async (BrowserManager m, CancellationToken ct) =>
        {
            var tabs = await m.SyncTabsAsync(ct);
            return Results.Json(tabs, JsonOpts);
        });

        // ── Screencast view: stream a tab into the cmux3 panel ───────────
        // Client connects to /ws/browser/view?url=<rawUrl>&tabId=<optional>.
        // Server attaches to the headless tab, pipes JPEG frames down, and
        // forwards input messages up. Message protocol (text):
        //   server → client:  "F" + base64Jpeg            (frame)
        //                      "E" + message               (error)
        //   client → server:  JSON { t:"mouse"|"key"|"wheel"|"viewport", ... }
        app.Map("/ws/browser/view", async (HttpContext ctx, BrowserManager m) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var sendLock = new SemaphoreSlim(1, 1);

            async Task Send(string s)
            {
                var bytes = Encoding.UTF8.GetBytes(s);
                await sendLock.WaitAsync();
                try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
                finally { sendLock.Release(); }
            }

            var url = ctx.Request.Query["url"].FirstOrDefault();
            var tabId = ctx.Request.Query["tabId"].FirstOrDefault();
            var forceNew = ctx.Request.Query["new"].FirstOrDefault() == "1";

            BrowserScreencastSession? cast = null;
            try
            {
                var target = await m.GetDebuggerTargetAsync(tabId, url, forceNew, CancellationToken.None);
                if (target == null)
                {
                    await Send("ECould not open a browser tab to stream.");
                    return;
                }

                cast = new BrowserScreencastSession();
                cast.FrameReceived += async (b64) => await Send("F" + b64);
                cast.MetaReceived += async (title, url) =>
                    await Send("M" + JsonSerializer.Serialize(new { title, url }, JsonOpts));
                await cast.StartAsync(target.Value.debuggerWsUrl, CancellationToken.None);
                await Send("R" + target.Value.tabId); // ready + resolved tab id

                var buf = new byte[1 << 15];
                var sb = new StringBuilder();
                while (socket.State == WebSocketState.Open)
                {
                    sb.Clear();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buf, CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    await HandleInput(cast, sb.ToString());
                }
            }
            catch (BrowserUnavailableException ex) { await Send("E" + ex.Message); }
            catch (WebSocketException) { }
            catch (Exception ex) { try { await Send("E" + ex.Message); } catch { } }
            finally { if (cast != null) await cast.DisposeAsync(); }
        });

        // ── Persistent WebSocket (fan-out events to the browser panel) ───

        app.Map("/ws/browser", async (HttpContext ctx, BrowserManager m) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            { ctx.Response.StatusCode = 400; return; }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var sendLock = new SemaphoreSlim(1, 1);
            var subId = m.Subscribe(async (evt, payload) =>
            {
                var msg = JsonSerializer.Serialize(new { evt, payload }, JsonOpts);
                var bytes = Encoding.UTF8.GetBytes(msg);
                await sendLock.WaitAsync();
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally { sendLock.Release(); }
            });

            async Task Send(string prefix, object? data)
            {
                var raw = data != null ? JsonSerializer.Serialize(data, JsonOpts) : "";
                var bytes = Encoding.UTF8.GetBytes(prefix + raw);
                await sendLock.WaitAsync();
                try
                {
                    if (socket.State == WebSocketState.Open)
                        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                finally { sendLock.Release(); }
            }

            try
            {
                // Send current state immediately on connect.
                await Send("L", m.GetTabs());

                var buf = new byte[4096];
                while (socket.State == WebSocketState.Open)
                {
                    var result = await socket.ReceiveAsync(buf, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.Count < 2) continue;

                    var msg = Encoding.UTF8.GetString(buf, 0, result.Count);
                    var cmd = msg[0];
                    var arg = msg.Length > 1 ? msg[1..] : "";

                    try
                    {
                        switch (cmd)
                        {
                            case 'c': // open
                                await Send("O", await m.OpenTabAsync(arg, CancellationToken.None));
                                break;
                            case 'f': // focus
                                await m.FocusTabAsync(arg, CancellationToken.None);
                                await Send("O", m.GetActiveTab());
                                break;
                            case 'x': // close
                                await m.CloseTabAsync(arg, CancellationToken.None);
                                break;
                            case 'r': // reload
                                await m.ReloadTabAsync(arg, CancellationToken.None);
                                break;
                            case 's': // sync
                                await Send("L", await m.SyncTabsAsync(CancellationToken.None));
                                break;
                            case 'a': // active tab
                                await Send("O", m.GetActiveTab());
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        await Send("E", ex.Message);
                    }
                }
            }
            catch (WebSocketException) { /* client dropped */ }
            finally { m.Unsubscribe(subId); }
        });
    }

    private static async Task HandleInput(BrowserScreencastSession cast, string raw)
    {
        JsonObject? parsed;
        try { parsed = JsonNode.Parse(raw) as JsonObject; }
        catch { return; }
        if (parsed is not JsonObject node) return;
        var t = node["t"]?.GetValue<string>();
        var ct = CancellationToken.None;
        try
        {
            switch (t)
            {
                case "mouse":
                    await cast.DispatchMouseAsync(
                        node["type"]?.GetValue<string>() ?? "mouseMoved",
                        node["x"]?.GetValue<double>() ?? 0,
                        node["y"]?.GetValue<double>() ?? 0,
                        node["button"]?.GetValue<string>() ?? "none",
                        node["clickCount"]?.GetValue<int>() ?? 0,
                        0, 0, ct);
                    break;
                case "wheel":
                    await cast.DispatchMouseAsync("mouseWheel",
                        node["x"]?.GetValue<double>() ?? 0,
                        node["y"]?.GetValue<double>() ?? 0,
                        "none", 0,
                        node["deltaX"]?.GetValue<double>() ?? 0,
                        node["deltaY"]?.GetValue<double>() ?? 0, ct);
                    break;
                case "key":
                    await cast.DispatchKeyAsync(
                        node["type"]?.GetValue<string>() ?? "keyDown",
                        node["key"]?.GetValue<string>() ?? "",
                        node["code"]?.GetValue<string>() ?? "",
                        node["keyCode"]?.GetValue<int>() ?? 0,
                        node["text"]?.GetValue<string>(), ct);
                    break;
                case "viewport":
                    await cast.SetViewportAsync(
                        node["width"]?.GetValue<int>() ?? 1280,
                        node["height"]?.GetValue<int>() ?? 800,
                        node["dpr"]?.GetValue<double>() ?? 1, ct);
                    break;
                case "navigate":
                    var navUrl = node["url"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(navUrl)) await cast.NavigateAsync(navUrl, ct);
                    break;
                case "reload":
                    await cast.ReloadAsync(ct);
                    break;
                case "back":
                    await cast.HistoryGoAsync(-1, ct);
                    break;
                case "forward":
                    await cast.HistoryGoAsync(1, ct);
                    break;
            }
        }
        catch { /* ignore malformed input */ }
    }
}

public record OpenTabReq(string? Url);












