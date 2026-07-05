using System.Net.WebSockets;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Wimux.Core.Config;
using Wimux.Web.Services;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Wimux.Tests")]

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AppStateStore>();
builder.Services.AddSingleton<CommandHistoryStore>();
builder.Services.AddSingleton<Wimux.Core.Services.NotificationService>();
builder.Services.AddSingleton<Wimux.Core.Services.CommandLogService>();
builder.Services.AddSingleton<Wimux.Core.Services.SnippetService>();
builder.Services.AddSingleton<Wimux.Core.Services.WorkspaceTemplateService>();
builder.Services.AddSingleton<Wimux.Core.Services.AgentConversationStoreService>();
builder.Services.AddSingleton<Wimux.Core.Services.AgentQuotaService>();
builder.Services.AddSingleton<Wimux.Web.Services.AgentRuntimeService>();
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddSingleton<Wimux.Web.Services.Browser.IBrowserProvider>(_ => new Wimux.Web.Services.Browser.ChromeCdpProvider());
builder.Services.AddSingleton<Wimux.Web.Services.Browser.BrowserManager>();
builder.Services.AddHttpClient("frame-proxy", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126 Safari/537.36");
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Never let the browser cache index.html — otherwise a stale shell keeps
// loading an old hashed JS bundle after an update.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var ct = context.Response.ContentType ?? "";
        if (ct.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Task.CompletedTask;
    });
    await next();
});

app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

// ── State ───────────────────────────────────────────────────────────
app.MapGet("/api/state", (AppStateStore store) => Results.Json(store.State, json));

app.MapPost("/api/workspaces", (AppStateStore store, CreateWorkspaceReq req) =>
{
    var name = string.IsNullOrWhiteSpace(req.Name)
        ? AppStateStore.NextNumberedName(store.State.Workspaces.Select(w => w.Name), AppStateStore.DefaultWorkspaceNamePrefix)
        : AppStateStore.NextUniqueName(store.State.Workspaces.Select(w => w.Name), req.Name.Trim());

    var pane = new PaneDto { Type = "terminal" };
    var surface = new SurfaceDto
    {
        Name = $"{AppStateStore.DefaultTerminalNamePrefix} 1",
        Root = new SplitNodeDto { IsLeaf = true, PaneId = pane.Id },
        FocusedPaneId = pane.Id,
        Panes = { [pane.Id] = pane },
    };
    var ws = new WorkspaceDto
    {
        Name = name,
        WorkingDirectory = req.WorkingDirectory,
        Surfaces = { surface },
        SelectedSurfaceId = surface.Id,
    };
    store.Mutate(s => { s.Workspaces.Add(ws); s.SelectedWorkspaceId = ws.Id; });
    return Results.Json(ws, json);
});

app.MapDelete("/api/workspaces/{id}", (AppStateStore store, TerminalSessionManager term, string id) =>
{
    var ws = store.FindWorkspace(id);
    if (ws == null) return Results.NotFound();
    foreach (var surface in ws.Surfaces)
        foreach (var pane in SplitTreeOps.AllPanes(surface.Root))
            term.Close(pane);
    store.Mutate(s =>
    {
        s.Workspaces.RemoveAll(w => w.Id == id);
        if (s.SelectedWorkspaceId == id)
            s.SelectedWorkspaceId = s.Workspaces.FirstOrDefault()?.Id;
    });
    return Results.Ok();
});

app.MapPost("/api/workspaces/{id}/select", (AppStateStore store, string id) =>
{
    if (store.FindWorkspace(id) == null) return Results.NotFound();
    store.Mutate(s => s.SelectedWorkspaceId = id);
    return Results.Ok();
});

app.MapPut("/api/workspaces/{id}", (AppStateStore store, string id, UpdateWorkspaceReq req) =>
{
    var ws = store.FindWorkspace(id);
    if (ws == null) return Results.NotFound();
    store.Mutate(_ =>
    {
        if (req.Name != null) ws.Name = req.Name;
        if (req.AccentColor != null) ws.AccentColor = req.AccentColor;
        if (req.WorkingDirectory != null) ws.WorkingDirectory = req.WorkingDirectory;
    });
    return Results.Json(ws, json);
});

// ── Surfaces ────────────────────────────────────────────────────────
app.MapPost("/api/workspaces/{wsId}/surfaces", (AppStateStore store, string wsId, CreateSurfaceReq req) =>
{
    var ws = store.FindWorkspace(wsId);
    if (ws == null) return Results.NotFound();
    var paneType = string.IsNullOrWhiteSpace(req?.Type) ? "terminal" : req!.Type!;
    var pane = new PaneDto
    {
        Type = paneType,
        Shell = paneType == "terminal" ? req?.Shell : null,
        Url = paneType == "web" ? req?.Url : null,
        Title = paneType == "web" ? "Browser" : null,
    };
    var name = string.IsNullOrWhiteSpace(req?.Name)
        ? paneType == "web"
            ? AppStateStore.NextNumberedName(ws.Surfaces.Select(s => s.Name), "Browser")
            : AppStateStore.NextNumberedName(ws.Surfaces.Select(s => s.Name), AppStateStore.DefaultTerminalNamePrefix)
        : AppStateStore.NextUniqueName(ws.Surfaces.Select(s => s.Name), req!.Name.Trim());
    var surface = new SurfaceDto
    {
        Name = name,
        Root = new SplitNodeDto { IsLeaf = true, PaneId = pane.Id },
        FocusedPaneId = pane.Id,
        Panes = { [pane.Id] = pane },
    };
    store.Mutate(_ => { ws.Surfaces.Add(surface); ws.SelectedSurfaceId = surface.Id; });
    return Results.Json(surface, json);
});

app.MapPost("/api/workspaces/{wsId}/surfaces/{sId}/select", (AppStateStore store, string wsId, string sId) =>
{
    var ws = store.FindWorkspace(wsId);
    if (ws == null || ws.Surfaces.All(s => s.Id != sId)) return Results.NotFound();
    store.Mutate(_ => ws.SelectedSurfaceId = sId);
    return Results.Ok();
});

app.MapPut("/api/workspaces/{wsId}/surfaces/{sId}", (AppStateStore store, string wsId, string sId, RenameReq req) =>
{
    var surface = store.FindSurface(wsId, sId);
    if (surface == null) return Results.NotFound();
    store.Mutate(_ => surface.Name = req.Name);
    return Results.Json(surface, json);
});

app.MapDelete("/api/workspaces/{wsId}/surfaces/{sId}", (AppStateStore store, TerminalSessionManager term, string wsId, string sId) =>
{
    var ws = store.FindWorkspace(wsId);
    var surface = ws?.Surfaces.FirstOrDefault(s => s.Id == sId);
    if (ws == null || surface == null) return Results.NotFound();
    foreach (var pane in SplitTreeOps.AllPanes(surface.Root))
        term.Close(pane);
    store.Mutate(_ =>
    {
        ws.Surfaces.RemoveAll(s => s.Id == sId);
        if (ws.SelectedSurfaceId == sId)
            ws.SelectedSurfaceId = ws.Surfaces.FirstOrDefault()?.Id;
    });
    return Results.Ok();
});

// ── Panes / splits ──────────────────────────────────────────────────
app.MapPost("/api/workspaces/{wsId}/surfaces/{sId}/split", (AppStateStore store, string wsId, string sId, SplitReq req) =>
{
    var surface = store.FindSurface(wsId, sId);
    if (surface == null) return Results.NotFound();
    string? newId = null;
    store.Mutate(_ =>
    {
        var paneType = string.IsNullOrWhiteSpace(req.Type) ? "terminal" : req.Type!;
        var pane = new PaneDto
        {
            Type = paneType,
            Shell = paneType == "terminal" ? req.Shell : null,
            Url = paneType == "web" ? req.Url : null,
            Title = paneType == "web" ? "Browser" : null,
        };
        newId = SplitTreeOps.Split(surface, req.PaneId, req.Direction, pane);
        if (newId != null) surface.FocusedPaneId = newId;
    });
    if (newId == null) return Results.BadRequest();
    return Results.Json(surface, json);
});

app.MapDelete("/api/workspaces/{wsId}/surfaces/{sId}/panes/{paneId}", (AppStateStore store, TerminalSessionManager term, string wsId, string sId, string paneId) =>
{
    var surface = store.FindSurface(wsId, sId);
    if (surface == null) return Results.NotFound();
    term.Close(paneId);
    store.Mutate(_ =>
    {
        var focus = SplitTreeOps.RemovePane(surface, paneId);
        if (surface.FocusedPaneId == paneId) surface.FocusedPaneId = focus;
    });
    return Results.Json(surface, json);
});

app.MapPost("/api/workspaces/{wsId}/surfaces/{sId}/focus/{paneId}", (AppStateStore store, string wsId, string sId, string paneId) =>
{
    var surface = store.FindSurface(wsId, sId);
    if (surface == null) return Results.NotFound();
    store.Mutate(_ => surface.FocusedPaneId = paneId);
    return Results.Ok();
});

app.MapPost("/api/workspaces/{wsId}/surfaces/{sId}/ratio", (AppStateStore store, string wsId, string sId, RatioReq req) =>
{
    var surface = store.FindSurface(wsId, sId);
    if (surface == null) return Results.NotFound();
    store.Mutate(_ =>
    {
        var node = FindNodeById(surface.Root, req.NodeId);
        if (node != null) node.SplitRatio = Math.Clamp(req.Ratio, 0.1, 0.9);
    });
    return Results.Ok();
});

// ── Pane type (terminal | web | notepad) ────────────────────────────
app.MapPut("/api/workspaces/{wsId}/surfaces/{sId}/panes/{paneId}", (
    AppStateStore store, TerminalSessionManager term,
    string wsId, string sId, string paneId, UpdatePaneReq req) =>
{
    var surface = store.FindSurface(wsId, sId);
    if (surface == null || !surface.Panes.TryGetValue(paneId, out var pane)) return Results.NotFound();
    store.Mutate(_ =>
    {
        if (req.Type != null) pane.Type = req.Type;
        if (req.Url != null) pane.Url = req.Url;
        if (req.Notes != null) pane.Notes = req.Notes;
    });
    // A pane that is no longer a terminal should release its shell.
    if (req.Type != null && req.Type != "terminal")
        term.Close(paneId);
    return Results.Json(pane, json);
});
// ── Settings + shells + themes ──────────────────────────────────────
app.MapGet("/api/settings", () => Results.Json(SettingsService.Current, json));
app.MapPut("/api/settings", (WimuxSettings settings) =>
{
    SettingsService.Save(settings);
    return Results.Json(SettingsService.Current, json);
});
app.MapGet("/api/shells", () => Results.Json(Wimux.Core.Services.ShellDetector.DetectShells(), json));
app.MapGet("/api/themes", () =>
{
    var themes = TerminalThemes.BuiltIn.Values.Select(t => new
    {
        name = t.Name,
        background = TerminalThemes.ToHex(t.Background),
        foreground = TerminalThemes.ToHex(t.Foreground),
        cursor = TerminalThemes.ToHex(t.CursorColor),
        selection = TerminalThemes.ToHex(t.SelectionBg),
        palette = t.Palette.Select(TerminalThemes.ToHex).ToArray(),
    });
    return Results.Json(themes, json);
});

// ── Notifications ───────────────────────────────────────────────────
app.MapGet("/api/notifications", (Wimux.Core.Services.NotificationService svc) =>
    Results.Json(new { items = svc.Notifications.ToArray(), unread = svc.UnreadCount }, json));
app.MapPost("/api/notifications", (Wimux.Core.Services.NotificationService svc, NotifyReq req) =>
{
    svc.AddNotification(req.WorkspaceId ?? "", req.SurfaceId ?? "", req.PaneId,
        req.Title ?? "Terminal", req.Subtitle, req.Body ?? "", Wimux.Core.Models.NotificationSource.Cli);
    return Results.Ok();
});
app.MapPost("/api/notifications/{id}/read", (Wimux.Core.Services.NotificationService svc, string id) =>
{ svc.MarkAsRead(id); return Results.Ok(); });
app.MapPost("/api/notifications/read-all", (Wimux.Core.Services.NotificationService svc) =>
{ svc.MarkAllAsRead(); return Results.Ok(); });
app.MapDelete("/api/notifications", (Wimux.Core.Services.NotificationService svc) =>
{ svc.Clear(); return Results.Ok(); });

// ── Command logs / history / transcripts ────────────────────────────
app.MapGet("/api/logs/dates", (Wimux.Core.Services.CommandLogService svc) =>
    Results.Json(svc.GetAvailableDates().Select(d => d.ToString("yyyy-MM-dd")), json));
app.MapGet("/api/logs", (Wimux.Core.Services.CommandLogService svc, string? date, string? q) =>
{
    if (!string.IsNullOrWhiteSpace(q)) return Results.Json(svc.Search(q), json);
    var d = DateOnly.TryParse(date, out var parsed) ? parsed : DateOnly.FromDateTime(DateTime.Now);
    return Results.Json(svc.GetForDate(d), json);
});
app.MapGet("/api/history", (CommandHistoryStore svc, string? paneId) =>
    Results.Json(string.IsNullOrWhiteSpace(paneId) ? svc.GetAll() : svc.Get(paneId), json));

// Best-effort iframe proxy. This strips frame-blocking response headers and rewrites
// relative URLs via <base>. Complex apps may still break due cookies/CORS/client CSP.
app.MapGet("/api/frame-proxy", async (IHttpClientFactory factory, string url, HttpContext ctx) =>
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
        (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        return Results.BadRequest("Invalid url");

    var client = factory.CreateClient("frame-proxy");
    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
    req.Headers.Referrer = uri;
    req.Headers.TryAddWithoutValidation("Accept", ctx.Request.Headers.Accept.ToString() is { Length: > 0 } accept ? accept : "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    req.Headers.TryAddWithoutValidation("Accept-Language", ctx.Request.Headers.AcceptLanguage.ToString() is { Length: > 0 } lang ? lang : "en-US,en;q=0.9");

    using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
    var bytes = await resp.Content.ReadAsByteArrayAsync(ctx.RequestAborted);

    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers.Remove("X-Frame-Options");
    ctx.Response.Headers.Remove("Content-Security-Policy");
    ctx.Response.Headers.Remove("Content-Security-Policy-Report-Only");
    ctx.Response.Headers.Remove("Cross-Origin-Opener-Policy");
    ctx.Response.Headers.Remove("Cross-Origin-Embedder-Policy");

    if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
    {
        var html = Encoding.UTF8.GetString(bytes);
        html = Regex.Replace(html, @"<meta[^>]+http-equiv\s*=\s*[""']?Content-Security-Policy[""']?[^>]*>", "", RegexOptions.IgnoreCase);
        var baseTag = $"""<base href="{uri.GetLeftPart(UriPartial.Path)}">""";
        html = Regex.IsMatch(html, "<head[^>]*>", RegexOptions.IgnoreCase)
            ? Regex.Replace(html, "<head([^>]*)>", $"<head$1>{baseTag}", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100))
            : baseTag + html;
        return Results.Content(html, "text/html; charset=utf-8");
    }

    return Results.File(bytes, resp.Content.Headers.ContentType?.ToString() ?? mediaType);
});

app.MapGet("/api/transcripts", (Wimux.Core.Services.CommandLogService svc) =>
    Results.Json(svc.GetTerminalTranscripts(), json));
app.MapGet("/api/transcripts/content", (Wimux.Core.Services.CommandLogService svc, string path) =>
    Results.Text(svc.LoadTerminalTranscriptContent(path)));
app.MapPost("/api/panes/{paneId}/capture", (TerminalSessionManager term, string paneId) =>
{
    var file = term.CaptureTranscript(paneId, "manual");
    return file == null ? Results.NotFound() : Results.Json(new { file }, json);
});
app.MapGet("/api/panes/{paneId}/input-trace", (TerminalSessionManager term, string paneId) =>
    Results.Json(term.GetInputTrace(paneId), json));
app.MapDelete("/api/panes/{paneId}/input-trace", (TerminalSessionManager term, string paneId) =>
{
    term.ClearInputTrace(paneId);
    return Results.Ok();
});
app.MapGet("/api/terminal/focused/input-trace", (AppStateStore store, TerminalSessionManager term) =>
{
    var ws = store.State.Workspaces.FirstOrDefault(w => w.Id == store.State.SelectedWorkspaceId)
             ?? store.State.Workspaces.FirstOrDefault();
    var surface = ws?.Surfaces.FirstOrDefault(s => s.Id == ws.SelectedSurfaceId)
                  ?? ws?.Surfaces.FirstOrDefault();
    var paneId = surface?.FocusedPaneId;
    return string.IsNullOrWhiteSpace(paneId)
        ? Results.NotFound()
        : Results.Json(new { paneId, items = term.GetInputTrace(paneId) }, json);
});
app.MapDelete("/api/terminal/focused/input-trace", (AppStateStore store, TerminalSessionManager term) =>
{
    var ws = store.State.Workspaces.FirstOrDefault(w => w.Id == store.State.SelectedWorkspaceId)
             ?? store.State.Workspaces.FirstOrDefault();
    var surface = ws?.Surfaces.FirstOrDefault(s => s.Id == ws.SelectedSurfaceId)
                  ?? ws?.Surfaces.FirstOrDefault();
    var paneId = surface?.FocusedPaneId;
    if (string.IsNullOrWhiteSpace(paneId)) return Results.NotFound();
    term.ClearInputTrace(paneId);
    return Results.Ok();
});

// ── Snippets ────────────────────────────────────────────────────────
app.MapGet("/api/snippets", (Wimux.Core.Services.SnippetService svc, string? q) =>
    Results.Json(svc.Search(q ?? ""), json));
app.MapGet("/api/snippets/categories", (Wimux.Core.Services.SnippetService svc) =>
    Results.Json(svc.GetCategories(), json));
app.MapPost("/api/snippets", (Wimux.Core.Services.SnippetService svc, Wimux.Core.Models.Snippet snippet) =>
{ svc.Add(snippet); return Results.Json(snippet, json); });
app.MapPut("/api/snippets/{id}", (Wimux.Core.Services.SnippetService svc, string id, Wimux.Core.Models.Snippet snippet) =>
{ snippet.Id = id; svc.Update(snippet); return Results.Json(snippet, json); });
app.MapDelete("/api/snippets/{id}", (Wimux.Core.Services.SnippetService svc, string id) =>
{ svc.Delete(id); return Results.Ok(); });
app.MapPost("/api/snippets/{id}/use", (Wimux.Core.Services.SnippetService svc, string id) =>
{ svc.IncrementUseCount(id); return Results.Ok(); });

// ── Workspace templates ─────────────────────────────────────────────
app.MapGet("/api/templates", (Wimux.Core.Services.WorkspaceTemplateService svc) =>
    Results.Json(svc.GetTemplates(), json));
app.MapPost("/api/templates", (Wimux.Core.Services.WorkspaceTemplateService svc, Wimux.Core.Services.WorkspaceTemplate t) =>
{ svc.Save(t); return Results.Json(t, json); });
app.MapPost("/api/templates/from-workspace/{wsId}", (
    AppStateStore store, Wimux.Core.Services.WorkspaceTemplateService svc, string wsId, RenameReq req) =>
{
    var ws = store.FindWorkspace(wsId);
    if (ws == null) return Results.NotFound();
    var template = new Wimux.Core.Services.WorkspaceTemplate
    {
        Name = string.IsNullOrWhiteSpace(req?.Name) ? ws.Name : req!.Name,
        EnvironmentVariables = new Dictionary<string, string>(ws.EnvironmentVariables),
    };
    foreach (var surface in ws.Surfaces)
    {
        var ts = new Wimux.Core.Services.TemplateSurface { Name = surface.Name };
        foreach (var pane in SplitTreeOps.AllPanes(surface.Root))
        {
            surface.Panes.TryGetValue(pane, out var paneDto);
            ts.Panes.Add(new Wimux.Core.Services.TemplatePaneLayout
            {
                WorkingDirectory = paneDto?.WorkingDirectory,
            });
        }
        template.Surfaces.Add(ts);
    }
    svc.Save(template);
    return Results.Json(template, json);
});
app.MapDelete("/api/templates/{id}", (Wimux.Core.Services.WorkspaceTemplateService svc, string id) =>
{ svc.Delete(id); return Results.Ok(); });
app.MapPost("/api/templates/{id}/apply", (AppStateStore store, Wimux.Core.Services.WorkspaceTemplateService svc, string id) =>
{
    var template = svc.GetTemplates().FirstOrDefault(t => t.Id == id);
    if (template == null) return Results.NotFound();

    SurfaceDto BuildSurface(Wimux.Core.Services.TemplateSurface ts)
    {
        var panes = ts.Panes.Count > 0 ? ts.Panes : new List<Wimux.Core.Services.TemplatePaneLayout> { new() };
        var paneDtos = panes.Select(p => new PaneDto { Type = "terminal", WorkingDirectory = p.WorkingDirectory }).ToList();
        // Build a left-leaning split tree from the pane list.
        SplitNodeDto root = new() { IsLeaf = true, PaneId = paneDtos[0].Id };
        for (int i = 1; i < paneDtos.Count; i++)
        {
            root = new SplitNodeDto
            {
                IsLeaf = false,
                Direction = panes[i].Direction == Wimux.Core.Models.SplitDirection.Horizontal ? "horizontal" : "vertical",
                First = root,
                Second = new SplitNodeDto { IsLeaf = true, PaneId = paneDtos[i].Id },
            };
        }
        var surface = new SurfaceDto
        {
            Name = string.IsNullOrWhiteSpace(ts.Name) ? "Terminal" : ts.Name,
            Root = root,
            FocusedPaneId = paneDtos[0].Id,
        };
        foreach (var pd in paneDtos) surface.Panes[pd.Id] = pd;
        return surface;
    }

    var surfaces = (template.Surfaces.Count > 0
        ? template.Surfaces
        : new List<Wimux.Core.Services.TemplateSurface> { new() })
        .Select(BuildSurface).ToList();

    var ws = new WorkspaceDto
    {
        Name = template.Name,
        Surfaces = surfaces,
        SelectedSurfaceId = surfaces[0].Id,
        EnvironmentVariables = new Dictionary<string, string>(template.EnvironmentVariables),
    };
    store.Mutate(s => { s.Workspaces.Add(ws); s.SelectedWorkspaceId = ws.Id; });
    return Results.Json(ws, json);
});

// ── Agent quota ─────────────────────────────────────────────────────
app.MapGet("/api/quota", (Wimux.Core.Services.AgentQuotaService svc) =>
{
    var snap = svc.GetSnapshot();
    var windows = snap.RowsByWindow.ToDictionary(
        kv => kv.Key.ToString(),
        kv => new { rows = kv.Value, totalTokens = snap.TotalTokensFor(kv.Key), requests = snap.RequestsFor(kv.Key) });
    return Results.Json(new { generatedAtUtc = snap.GeneratedAtUtc, windows }, json);
});

// ── Git ─────────────────────────────────────────────────────────────
app.MapGet("/api/git/branch", (string? cwd) =>
    Results.Json(new { branch = Wimux.Core.Services.GitService.GetBranch(cwd), remote = Wimux.Core.Services.GitService.GetRemoteUrl(cwd) }, json));

// ── Ports ───────────────────────────────────────────────────────────
app.MapGet("/api/ports", (TerminalSessionManager term, string paneId) =>
{
    var session = term.Get(paneId);
    var pid = session?.ProcessId;
    return Results.Json(pid is int p ? Wimux.Core.Services.PortScanner.GetListeningPorts(p) : new List<int>(), json);
});
// ── External agents ─────────────────────────────────────────────────
app.MapGet("/api/agents", () =>
{
    var svc = new Wimux.Core.Services.ExternalAgentService();
    return Results.Json(svc.DetectAgents(), json);
});
app.MapGet("/api/agents/conversation", (string sessionFilePath, int? max) =>
{
    var svc = new Wimux.Core.Services.ExternalAgentService();
    var agent = new Wimux.Core.Models.ExternalAgentInfo { SessionFilePath = sessionFilePath };
    return Results.Json(svc.GetConversation(agent, max ?? 50), json);
});

app.MapPost("/api/agents/send", (TerminalSessionManager term, AppStateStore store, AgentExternalSendReq req) =>
{
    var text = req.Text?.Trim();
    if (string.IsNullOrWhiteSpace(text))
        return Results.BadRequest(new { ok = false, error = "Message is required." });

    var paneId = FindAgentPaneId(term, store, req.Pid, req.ProjectPath);
    if (string.IsNullOrWhiteSpace(paneId))
        return Results.NotFound(new { ok = false, error = "No matching terminal pane found for this agent." });

    SubmitToAgentSession(term, paneId, text);
    return Results.Json(new { ok = true, paneId }, json);
});

// ── Agent conversation threads ──────────────────────────────────────
app.MapGet("/api/threads", (
    Wimux.Core.Services.AgentConversationStoreService svc,
    string? workspaceId,
    string? surfaceId,
    string? paneId,
    string? q) =>
{
    var rows = string.IsNullOrWhiteSpace(q)
        ? svc.GetThreads(workspaceId ?? "", surfaceId ?? "", paneId ?? "")
        : svc.SearchThreads(workspaceId ?? "", surfaceId ?? "", paneId ?? "", q);
    return Results.Json(rows, json);
});
app.MapPost("/api/threads", (Wimux.Core.Services.AgentConversationStoreService svc, AgentRuntimeService agent, AppStateStore store, AgentThreadCreateReq req) =>
{
    var ctx = store.FindPaneContext(req.PaneId);
    var thread = svc.CreateThread(
        ctx?.Workspace.Id ?? "",
        ctx?.Surface.Id ?? "",
        req.PaneId,
        SettingsService.Current.Agent.AgentName);
    if (ctx != null)
        agent.SetActiveThreadId(ctx.Workspace.Id, ctx.Surface.Id, req.PaneId, thread.Id);
    return Results.Json(thread, json);
});
app.MapPost("/api/threads/{id}/activate", (Wimux.Core.Services.AgentConversationStoreService svc, AgentRuntimeService agent, string id, AgentThreadActivateReq req) =>
{
    var thread = svc.GetThread(id);
    if (thread == null) return Results.NotFound();
    agent.SetActiveThreadId(
        string.IsNullOrWhiteSpace(req.WorkspaceId) ? thread.WorkspaceId : req.WorkspaceId,
        string.IsNullOrWhiteSpace(req.SurfaceId) ? thread.SurfaceId : req.SurfaceId,
        string.IsNullOrWhiteSpace(req.PaneId) ? thread.PaneId : req.PaneId,
        thread.Id);
    return Results.Ok();
});
app.MapGet("/api/threads/{id}/messages", (Wimux.Core.Services.AgentConversationStoreService svc, string id) =>
    Results.Json(svc.GetMessages(id), json));
app.MapDelete("/api/threads/{threadId}/messages/{messageId}", (Wimux.Core.Services.AgentConversationStoreService svc, string threadId, string messageId) =>
    svc.DeleteMessage(threadId, messageId) ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/threads/{id}", (Wimux.Core.Services.AgentConversationStoreService svc, string id) =>
    svc.DeleteThread(id) ? Results.Ok() : Results.NotFound());

// ── Workspace environment variables ─────────────────────────────────
app.MapGet("/api/workspaces/{id}/env", (AppStateStore store, string id) =>
{
    var ws = store.FindWorkspace(id);
    return ws == null ? Results.NotFound() : Results.Json(ws.EnvironmentVariables, json);
});
app.MapPut("/api/workspaces/{id}/env", (AppStateStore store, string id, Dictionary<string, string> env) =>
{
    var ws = store.FindWorkspace(id);
    if (ws == null) return Results.NotFound();
    store.Mutate(_ => ws.EnvironmentVariables = env);
    return Results.Json(ws.EnvironmentVariables, json);
});

// ── Workspace SSH profiles ──────────────────────────────────────────
app.MapGet("/api/workspaces/{id}/ssh", (AppStateStore store, string id) =>
{
    var ws = store.FindWorkspace(id);
    return ws == null ? Results.NotFound() : Results.Json(ws.SshProfiles, json);
});
app.MapPut("/api/workspaces/{id}/ssh", (AppStateStore store, string id, List<SshProfileDto> profiles) =>
{
    var ws = store.FindWorkspace(id);
    if (ws == null) return Results.NotFound();
    store.Mutate(_ => ws.SshProfiles = profiles);
    return Results.Json(ws.SshProfiles, json);
});

app.MapPost("/api/dialog/open-file", async (string? initialDirectory) =>
{
    var path = await Program.ShowOpenFileDialogAsync(initialDirectory);
    return string.IsNullOrWhiteSpace(path)
        ? Results.NoContent()
        : Results.Json(new { path }, json);
});

app.MapPost("/api/clipboard/image-file", async (ClipboardImageReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Path) || !File.Exists(req.Path))
        return Results.NotFound();
    if (!Program.IsSupportedClipboardImage(req.Path))
        return Results.BadRequest(new { error = "File is not a supported image." });
    await Program.SetClipboardImageFileAsync(req.Path);
    return Results.Ok();
});

// ── Quick open (fuzzy file finder) ──────────────────────────────────
app.MapGet("/api/quick-open", (string root, string? q) =>
{
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.Json(Array.Empty<object>(), json);
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "dist", ".vs", ".idea", "__pycache__" };
    var files = new List<string>();
    void Walk(string dir, int depth)
    {
        if (depth > 6 || files.Count > 5000) return;
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { return; }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (!skip.Contains(name) && !name.StartsWith('.')) Walk(entry, depth + 1);
            }
            else files.Add(entry);
        }
    }
    Walk(root, 0);
    var rel = files.Select(f => new { fullPath = f, name = Path.GetRelativePath(root, f) }).ToList();
    if (string.IsNullOrWhiteSpace(q))
        return Results.Json(rel.Take(200), json);
    var ranked = Wimux.Core.Services.FuzzyMatcher
        .RankMatches(rel, q, x => x.name)
        .Take(200)
        .Select(r => r.Item);
    return Results.Json(ranked, json);
});
// ── Per-workspace status (git branch + unread) ─────────────────────
app.MapGet("/api/workspaces/status", (AppStateStore store, Wimux.Core.Services.NotificationService notif) =>
{
    var result = store.State.Workspaces.Select(w =>
    {
        string? cwd = w.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = w.Surfaces
                .SelectMany(s => s.Panes.Values)
                .Select(p => p.WorkingDirectory)
                .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));
        return new
        {
            id = w.Id,
            workingDirectory = cwd,
            branch = Wimux.Core.Services.GitService.GetBranch(cwd),
            unread = notif.GetUnreadCount(w.Id),
        };
    });
    return Results.Json(result, json);
});
// ── Agent runtime (chat with AI agent, streaming over SSE) ──────────
app.MapGet("/api/agent/settings", () => Results.Json(SettingsService.Current.Agent, json));
app.MapPut("/api/agent/settings", (Wimux.Core.Config.AgentSettings agent) =>
{
    var s = SettingsService.Current;
    s.Agent = agent;
    SettingsService.Save(s);
    return Results.Json(SettingsService.Current.Agent, json);
});
app.MapPut("/api/agent/secret", (AgentSecretReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest();
    Wimux.Core.Services.SecretStoreService.SetSecret(req.Name, req.Value);
    return Results.Ok();
});

app.MapPost("/api/agent/send", (AgentRuntimeService agent, TerminalSessionManager term, AppStateStore store, AgentSendReq req) =>
{
    var ctx = store.FindPaneContext(req.PaneId);
    var pane = ctx?.Pane;
    var context = new Wimux.Web.Services.AgentPaneContext
    {
        WorkspaceId = ctx?.Workspace.Id ?? "",
        SurfaceId = ctx?.Surface.Id ?? "",
        PaneId = req.PaneId,
        WorkingDirectory = pane?.WorkingDirectory ?? ctx?.Workspace.WorkingDirectory,
        WriteToPane = text => { if (pane?.Type == "terminal") term.Write(req.PaneId, System.Text.Encoding.UTF8.GetBytes(text)); },
        PaneContent = pane?.Type == "notepad" ? pane.Notes : pane?.Url,
        PaneTypeLabel = pane?.Type ?? "terminal",
    };
    var ok = agent.TrySendChatPrompt(req.Prompt, context, req.ThreadId);
    return ok ? Results.Json(new { ok = true, threadId = agent.GetActiveThreadId(context.WorkspaceId, context.SurfaceId, req.PaneId) }, json)
              : Results.Json(new { ok = false, error = "Agent disabled or busy" }, json);
});

app.Map("/ws/agent", async (HttpContext ctx, AgentRuntimeService agent) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var sendLock = new SemaphoreSlim(1, 1);
    async void OnUpdate(AgentRuntimeUpdate u)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(u, json));
            await sendLock.WaitAsync();
            try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { sendLock.Release(); }
        }
        catch { /* client gone */ }
    }
    agent.RuntimeUpdated += OnUpdate;
    try
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
    }
    catch (WebSocketException) { }
    finally { agent.RuntimeUpdated -= OnUpdate; }
});
// ── Terminal WebSocket ──────────────────────────────────────────────
app.Map("/ws/terminal/{paneId}", async (HttpContext ctx, TerminalSessionManager term, AppStateStore store, string paneId) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    var cols = int.TryParse(ctx.Request.Query["cols"], out var c) ? c : 120;
    var rows = int.TryParse(ctx.Request.Query["rows"], out var r) ? r : 30;
    var cwd = ctx.Request.Query["cwd"].FirstOrDefault();
    var shellArg = ctx.Request.Query["shell"].FirstOrDefault();

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    var sendLock = new SemaphoreSlim(1, 1);

    async Task SendText(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        await sendLock.WaitAsync();
        try { if (socket.State == WebSocketState.Open) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }

    var existed = term.Exists(paneId);
    string? startCommand = null;
    if (!string.IsNullOrWhiteSpace(shellArg)) startCommand = shellArg;
    else
    {
        foreach (var ws2 in store.State.Workspaces)
            foreach (var s2 in ws2.Surfaces)
                if (s2.Panes.TryGetValue(paneId, out var p2) && !string.IsNullOrWhiteSpace(p2.Shell))
                    startCommand = p2.Shell;
    }
    term.GetOrCreate(paneId, cols, rows, cwd, startCommand);

    // Replay buffered output so reconnects/refreshes see prior content.
    var recent = term.GetRecentOutput(paneId);
    if (existed && recent is { Length: > 0 })
        await SendText("o" + Convert.ToBase64String(recent));

    var subId = term.Subscribe(paneId,
        async data => await SendText("o" + Convert.ToBase64String(data)),
        async ev => await SendText("e" + JsonSerializer.Serialize(ev, json)));

    // Push the current mouse-tracking state to the client immediately. TUI
    // apps (claude/codex/opencode) enable DEC mouse modes during init, so the
    // client needs to know before the first right-click — otherwise the
    // browser default menu shows up briefly and the user sees a flash.
    var session = term.Get(paneId);
    if (session != null)
        await SendText("e" + JsonSerializer.Serialize(
            new TerminalSessionManager.TerminalEvent("mouseTracking", paneId,
                session.Buffer.MouseEnabled ? "1" : "0"), json));

    try
    {
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;
            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (msg.Length == 0) continue;
            var kind = msg[0];
            var payload = msg[1..];
            switch (kind)
            {
                case 'i': // input (base64)
                    term.Write(paneId, Convert.FromBase64String(payload));
                    break;
                case 'r': // resize "cols,rows"
                    var parts = payload.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out var nc) && int.TryParse(parts[1], out var nr))
                        term.Resize(paneId, nc, nr);
                    break;
            }
        }
    }
    catch (WebSocketException) { /* client dropped */ }
    finally
    {
        term.Unsubscribe(paneId, subId);
    }
});

// Bridge the agent runtime "wimux" tool to a minimal command surface.
{
    var agentRuntime = app.Services.GetRequiredService<AgentRuntimeService>();
    var store = app.Services.GetRequiredService<AppStateStore>();
    var term = app.Services.GetRequiredService<TerminalSessionManager>();
    var notif = app.Services.GetRequiredService<Wimux.Core.Services.NotificationService>();
    agentRuntime.CommandHandler = (command, args) =>
    {
        string Resp(object o) => JsonSerializer.Serialize(o, json);
        var ws = ResolveWorkspace(args);
        var surface = ws != null ? ResolveSurface(ws, args) : null;
        switch (command)
        {
            case "STATUS":
                return Task.FromResult(Resp(new { workspaces = store.State.Workspaces.Count, selected = ws?.Name }));
            case "WORKSPACE.LIST":
                return Task.FromResult(Resp(store.State.Workspaces.Select((w, i) => new { index = i + 1, id = w.Id, name = w.Name, selected = w.Id == store.State.SelectedWorkspaceId, surfaces = w.Surfaces.Count })));
            case "NOTIFY":
                notif.AddNotification(ws?.Id ?? "", surface?.Id ?? "", null,
                    args.GetValueOrDefault("title", "Agent"), args.GetValueOrDefault("subtitle"),
                    args.GetValueOrDefault("body", ""), Wimux.Core.Models.NotificationSource.Cli);
                return Task.FromResult(Resp(new { ok = true }));
            case "PANE.LIST":
            {
                if (ws == null) return Task.FromResult(Resp(new { error = "No workspace available." }));
                if (surface == null) return Task.FromResult(Resp(new { error = "No surface available in workspace." }));
                var panes = GetOrderedPaneIds(surface)
                    .Select((paneId, idx) =>
                    {
                        surface.Panes.TryGetValue(paneId, out var pane);
                        var session = term.Get(paneId);
                        return new
                        {
                            index = idx + 1,
                            id = paneId,
                            name = pane?.Title ?? $"Pane {idx + 1}",
                            type = pane?.Type ?? "terminal",
                            focused = string.Equals(surface.FocusedPaneId, paneId, StringComparison.Ordinal),
                            workingDirectory = session?.WorkingDirectory ?? pane?.WorkingDirectory ?? "",
                            url = pane?.Url ?? "",
                        };
                    });
                return Task.FromResult(Resp(new
                {
                    workspace = new { id = ws.Id, name = ws.Name },
                    surface = new { id = surface.Id, name = surface.Name },
                    panes,
                }));
            }
            case "PANE.READ":
            {
                if (ws == null) return Task.FromResult(Resp(new { error = "No workspace available." }));
                if (surface == null) return Task.FromResult(Resp(new { error = "No surface available in workspace." }));
                if (!TryResolvePane(surface, args, out var paneId, out var paneIndex, out var paneName, out var error))
                    return Task.FromResult(Resp(new { error }));

                surface.Panes.TryGetValue(paneId, out var pane);
                var lines = ParseClampedInt(args, "lines", 80, 1, 5000);
                var maxChars = ParseClampedInt(args, "maxChars", 20000, 512, 200000);
                var session = term.Get(paneId);
                var text = pane?.Type switch
                {
                    "web" => pane.Url ?? "",
                    "notepad" => pane.Notes ?? "",
                    _ => session?.Buffer.ExportPlainText(maxScrollbackLines: 20000) ?? "",
                };
                if (string.IsNullOrWhiteSpace(text) && session != null)
                {
                    var recent = term.GetRecentOutput(paneId);
                    if (recent is { Length: > 0 })
                        text = Encoding.UTF8.GetString(recent);
                }
                var tailText = TailLines(text, lines);
                if (tailText.Length > maxChars)
                    tailText = "..." + tailText[^maxChars..];
                return Task.FromResult(Resp(new
                {
                    ok = true,
                    workspaceId = ws.Id,
                    workspaceName = ws.Name,
                    surfaceId = surface.Id,
                    surfaceName = surface.Name,
                    paneId,
                    paneIndex,
                    paneName,
                    paneType = pane?.Type ?? "terminal",
                    lines,
                    maxChars,
                    text = tailText,
                    content = tailText,
                }));
            }
            case "PANE.WRITE":
            {
                if (surface == null) return Task.FromResult(Resp(new { error = "No surface available in workspace." }));
                if (!TryResolvePane(surface, args, out var paneId, out var paneIndex, out var paneName, out var error))
                    return Task.FromResult(Resp(new { error }));
                var text = args.GetValueOrDefault("text", "");
                if (args.TryGetValue("submit", out var submitRaw) && bool.TryParse(submitRaw, out var submit) && submit)
                    text = text.TrimEnd('\r', '\n') + ResolveSubmitSequence(args.GetValueOrDefault("submitKey"));
                term.Write(paneId, System.Text.Encoding.UTF8.GetBytes(text));
                return Task.FromResult(Resp(new { ok = true, paneId, paneIndex, paneName, bytes = text.Length }));
            }
            default:
                return Task.FromResult(Resp(new { error = $"Unsupported command: {command}" }));
        }

        WorkspaceDto? ResolveWorkspace(Dictionary<string, string> selector)
        {
            var selected = store.State.Workspaces.FirstOrDefault(w => w.Id == store.State.SelectedWorkspaceId)
                           ?? store.State.Workspaces.FirstOrDefault();
            if (selector.TryGetValue("workspaceId", out var workspaceId) && !string.IsNullOrWhiteSpace(workspaceId))
                return store.State.Workspaces.FirstOrDefault(w => string.Equals(w.Id, workspaceId, StringComparison.Ordinal));
            if (selector.TryGetValue("workspaceName", out var workspaceName) && !string.IsNullOrWhiteSpace(workspaceName))
                return store.State.Workspaces.FirstOrDefault(w => string.Equals(w.Name, workspaceName, StringComparison.OrdinalIgnoreCase))
                       ?? store.State.Workspaces.FirstOrDefault(w => w.Name.Contains(workspaceName, StringComparison.OrdinalIgnoreCase));
            if (selector.TryGetValue("workspaceIndex", out var indexRaw) && int.TryParse(indexRaw, out var requested) &&
                TryResolveCollectionIndex(requested, store.State.Workspaces.Count, out var idx))
                return store.State.Workspaces[idx];
            return selected;
        }

        SurfaceDto? ResolveSurface(WorkspaceDto workspace, Dictionary<string, string> selector)
        {
            var selected = workspace.Surfaces.FirstOrDefault(s => s.Id == workspace.SelectedSurfaceId)
                           ?? workspace.Surfaces.FirstOrDefault();
            if (selector.TryGetValue("surfaceId", out var surfaceId) && !string.IsNullOrWhiteSpace(surfaceId))
                return workspace.Surfaces.FirstOrDefault(s => string.Equals(s.Id, surfaceId, StringComparison.Ordinal));
            if (selector.TryGetValue("surfaceName", out var surfaceName) && !string.IsNullOrWhiteSpace(surfaceName))
                return workspace.Surfaces.FirstOrDefault(s => string.Equals(s.Name, surfaceName, StringComparison.OrdinalIgnoreCase))
                       ?? workspace.Surfaces.FirstOrDefault(s => s.Name.Contains(surfaceName, StringComparison.OrdinalIgnoreCase));
            if (selector.TryGetValue("surfaceIndex", out var indexRaw) && int.TryParse(indexRaw, out var requested) &&
                TryResolveCollectionIndex(requested, workspace.Surfaces.Count, out var idx))
                return workspace.Surfaces[idx];
            return selected;
        }

        bool TryResolvePane(SurfaceDto targetSurface, Dictionary<string, string> selector, out string paneId, out int paneIndex, out string paneName, out string error)
        {
            var panes = GetOrderedPaneIds(targetSurface)
                .Select((id, idx) => new { Id = id, Index = idx + 1, Name = targetSurface.Panes.TryGetValue(id, out var p) && !string.IsNullOrWhiteSpace(p.Title) ? p.Title! : $"Pane {idx + 1}" })
                .ToList();
            paneId = "";
            paneIndex = -1;
            paneName = "";
            error = "";
            if (panes.Count == 0)
            {
                error = "No panes available in surface.";
                return false;
            }

            string? target = null;
            selector.TryGetValue("paneId", out var requestedId);
            if (string.IsNullOrWhiteSpace(requestedId))
                selector.TryGetValue("pane", out requestedId);
            if (!string.IsNullOrWhiteSpace(requestedId))
            {
                target = panes.FirstOrDefault(p => string.Equals(p.Id, requestedId, StringComparison.Ordinal))?.Id;
                if (target == null)
                {
                    error = $"Pane id not found: {requestedId}";
                    return false;
                }
            }
            else if (selector.TryGetValue("paneName", out var requestedName) && !string.IsNullOrWhiteSpace(requestedName))
            {
                target = panes.FirstOrDefault(p => string.Equals(p.Name, requestedName, StringComparison.OrdinalIgnoreCase))?.Id;
                if (target == null)
                {
                    error = $"Pane name not found: {requestedName}";
                    return false;
                }
            }
            else if (selector.TryGetValue("paneIndex", out var indexRaw) && int.TryParse(indexRaw, out var requested) &&
                     TryResolveCollectionIndex(requested, panes.Count, out var idx))
            {
                target = panes[idx].Id;
            }
            else if (selector.ContainsKey("paneIndex"))
            {
                error = $"Pane index out of range: {selector.GetValueOrDefault("paneIndex")}";
                return false;
            }

            target ??= !string.IsNullOrWhiteSpace(targetSurface.FocusedPaneId)
                ? panes.FirstOrDefault(p => string.Equals(p.Id, targetSurface.FocusedPaneId, StringComparison.Ordinal))?.Id
                : null;
            target ??= panes[0].Id;
            var resolved = panes.First(p => p.Id == target);
            paneId = resolved.Id;
            paneIndex = resolved.Index;
            paneName = resolved.Name;
            return true;
        }

        List<string> GetOrderedPaneIds(SurfaceDto targetSurface)
            => SplitTreeOps.AllPanes(targetSurface.Root)
                .Where(id => targetSurface.Panes.ContainsKey(id))
                .ToList();

        static bool TryResolveCollectionIndex(int requested, int count, out int zeroBasedIndex)
        {
            zeroBasedIndex = -1;
            if (count <= 0) return false;
            if (requested >= 1 && requested <= count) { zeroBasedIndex = requested - 1; return true; }
            if (requested >= 0 && requested < count) { zeroBasedIndex = requested; return true; }
            return false;
        }

        static int ParseClampedInt(Dictionary<string, string> values, string key, int fallback, int min, int max)
            => values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed)
                ? Math.Clamp(parsed, min, max)
                : fallback;

        static string TailLines(string? text, int lines)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var split = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            return string.Join("\n", split.TakeLast(Math.Max(1, lines))).TrimEnd();
        }

        static string ResolveSubmitSequence(string? submitKey)
        {
            var key = (submitKey ?? "auto").Trim().ToLowerInvariant();
            return key switch
            {
                "" or "auto" or "enter" or "cr" or "ctrl+m" => "\r",
                "linefeed" or "lf" or "ctrl+j" => "\n",
                "crlf" => "\r\n",
                "none" => "",
                _ => "\r",
            };
        }
    };
}
app.MapBrowserEndpoints();
app.MapFallbackToFile("index.html");

app.Run();

// ── Request records ─────────────────────────────────────────────────
record CreateWorkspaceReq(string? Name, string? WorkingDirectory);
record UpdateWorkspaceReq(string? Name, string? AccentColor, string? WorkingDirectory);
record CreateSurfaceReq(string? Name, string? Shell, string? Type, string? Url);
record RenameReq(string Name);
record SplitReq(string PaneId, string Direction, string? Shell, string? Type, string? Url);
record RatioReq(string NodeId, double Ratio);
record NotifyReq(string? WorkspaceId, string? SurfaceId, string? PaneId, string? Title, string? Subtitle, string? Body);
record UpdatePaneReq(string? Type, string? Url, string? Notes);
record AgentSecretReq(string Name, string? Value);
record AgentThreadCreateReq(string PaneId);
record AgentThreadActivateReq(string? WorkspaceId, string? SurfaceId, string? PaneId);
record AgentSendReq(string PaneId, string Prompt, string? ThreadId);
record AgentExternalSendReq(int Pid, string? ProjectPath, string? Text);
record ClipboardImageReq(string Path);

public partial class Program
{
    public static bool IsSupportedClipboardImage(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    public static async Task SetClipboardImageFileAsync(string path)
    {
        try
        {
            await SetClipboardImageFileOutOfProcessAsync(path);
            return;
        }
        catch
        {
            // Fall back to in-process STA clipboard access if PowerShell is not
            // available or blocked.
        }

        await SetClipboardImageFileInProcessAsync(path);
    }

    private static async Task SetClipboardImageFileOutOfProcessAsync(string path)
    {
        var script = """
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$path = [Console]::In.ReadToEnd()
if (-not [System.IO.File]::Exists($path)) { exit 2 }
$img = [System.Drawing.Image]::FromFile($path)
$bmp = New-Object System.Drawing.Bitmap($img)
$img.Dispose()
[System.Windows.Forms.Clipboard]::SetImage($bmp)
$bmp.Dispose()
""";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        proc.Start();
        await proc.StandardInput.WriteAsync(path);
        proc.StandardInput.Close();
        var errorTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(await errorTask);
    }

    private static Task SetClipboardImageFileInProcessAsync(string path)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var image = System.Drawing.Image.FromFile(path);
                using var bitmap = new System.Drawing.Bitmap(image);
                System.Windows.Forms.Clipboard.SetImage(bitmap);
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    public static async Task<string?> ShowOpenFileDialogAsync(string? initialDirectory)
    {
        return await ShowOpenFileDialogInProcessAsync(initialDirectory);
    }

    private static Task<string?> ShowOpenFileDialogInProcessAsync(string? initialDirectory)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(NativeOpenFileDialog.Show(initialDirectory));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static class NativeOpenFileDialog
    {
        private const uint FOS_FILEMUSTEXIST = 0x00001000;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

        public static string? Show(string? initialDirectory)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dialog.GetOptions(out var options);
                dialog.SetOptions(options | FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM);
                dialog.SetTitle("Choose file");

                var dir = ResolveDialogInitialDirectory(initialDirectory);
                if (!string.IsNullOrWhiteSpace(dir) &&
                    SHCreateItemFromParsingName(dir, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
                {
                    try { dialog.SetFolder(folder); }
                    finally { Marshal.ReleaseComObject(folder); }
                }

                var hr = dialog.Show(GetForegroundWindow());
                if (hr == HRESULT_CANCELLED) return null;
                Marshal.ThrowExceptionForHR(hr);

                dialog.GetResult(out var result);
                try
                {
                    result.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
                    try { return Marshal.PtrToStringUni(pathPtr); }
                    finally { Marshal.FreeCoTaskMem(pathPtr); }
                }
                finally
                {
                    Marshal.ReleaseComObject(result);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }

    private static string? ResolveDialogInitialDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path)) return Path.GetFullPath(path);
            if (File.Exists(path))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) ? parent : null;
            }
        }
        catch { /* ignore invalid caller-supplied path */ }
        return null;
    }

    static SplitNodeDto? FindNodeById(SplitNodeDto node, string id)
    {
        if (node.Id == id) return node;
        if (node.IsLeaf) return null;
        return (node.First != null ? FindNodeById(node.First, id) : null)
            ?? (node.Second != null ? FindNodeById(node.Second, id) : null);
    }

    static string? FindAgentPaneId(TerminalSessionManager term, AppStateStore store, int agentPid, string? projectPath)
    {
        foreach (var paneId in term.ActivePanes)
        {
            var session = term.Get(paneId);
            if (session?.ProcessId is int shellPid && IsChildProcess(agentPid, shellPid))
                return paneId;
        }

        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            string? target;
            try { target = Path.GetFullPath(projectPath); }
            catch { target = projectPath; }

            foreach (var paneId in term.ActivePanes)
            {
                var session = term.Get(paneId);
                if (session == null || string.IsNullOrWhiteSpace(session.WorkingDirectory))
                    continue;

                string cwd;
                try { cwd = Path.GetFullPath(session.WorkingDirectory); }
                catch { cwd = session.WorkingDirectory; }

                if (string.Equals(cwd, target, StringComparison.OrdinalIgnoreCase))
                    return paneId;
            }

            foreach (var ws in store.State.Workspaces)
            foreach (var surface in ws.Surfaces)
            foreach (var pane in surface.Panes.Values)
            {
                if (string.IsNullOrWhiteSpace(pane.WorkingDirectory))
                    continue;

                string cwd;
                try { cwd = Path.GetFullPath(pane.WorkingDirectory); }
                catch { cwd = pane.WorkingDirectory; }

                if (string.Equals(cwd, target, StringComparison.OrdinalIgnoreCase) && term.Exists(pane.Id))
                    return pane.Id;
            }
        }

        return null;
    }

    static bool IsChildProcess(int childPid, int parentPid)
    {
        if (childPid <= 0 || parentPid <= 0)
            return false;

        try
        {
            var visited = new HashSet<int>();
            var current = childPid;
            while (current > 0 && visited.Add(current))
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {current}");
                var parent = 0;
                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        if (obj["ParentProcessId"] is uint ppid)
                            parent = (int)ppid;
                    }
                    finally
                    {
                        obj.Dispose();
                    }
                }

                if (parent == parentPid)
                    return true;
                current = parent;
            }
        }
        catch { }

        return false;
    }

    static void SubmitToAgentSession(TerminalSessionManager term, string paneId, string text)
    {
        var body = text.Replace("\r\n", "\n").Replace('\r', '\n');
        term.Write(paneId, Encoding.UTF8.GetBytes(body + "\r"));

        if (!body.Contains('\n'))
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            term.Write(paneId, Encoding.UTF8.GetBytes("\r"));
        });
    }
}























