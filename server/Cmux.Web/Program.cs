using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Cmux.Core.Config;
using Cmux.Web.Services;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Cmux.Tests")]

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AppStateStore>();
builder.Services.AddSingleton<CommandHistoryStore>();
builder.Services.AddSingleton<Cmux.Core.Services.NotificationService>();
builder.Services.AddSingleton<Cmux.Core.Services.CommandLogService>();
builder.Services.AddSingleton<Cmux.Core.Services.SnippetService>();
builder.Services.AddSingleton<Cmux.Core.Services.WorkspaceTemplateService>();
builder.Services.AddSingleton<Cmux.Core.Services.AgentConversationStoreService>();
builder.Services.AddSingleton<Cmux.Core.Services.AgentQuotaService>();
builder.Services.AddSingleton<Cmux.Core.Services.KnowledgeGraphService>();
builder.Services.AddSingleton<Cmux.Web.Services.AgentRuntimeService>();
builder.Services.AddSingleton<TerminalSessionManager>();
builder.Services.AddSingleton<Cmux.Web.Services.Browser.IBrowserProvider>(_ => new Cmux.Web.Services.Browser.ChromeCdpProvider());
builder.Services.AddSingleton<Cmux.Web.Services.Browser.BrowserManager>();
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
    var pane = new PaneDto { Type = "terminal" };
    var surface = new SurfaceDto
    {
        Name = "Terminal",
        Root = new SplitNodeDto { IsLeaf = true, PaneId = pane.Id },
        FocusedPaneId = pane.Id,
        Panes = { [pane.Id] = pane },
    };
    var ws = new WorkspaceDto
    {
        Name = string.IsNullOrWhiteSpace(req.Name) ? "Workspace" : req.Name,
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
    var baseName = string.IsNullOrWhiteSpace(req?.Name) ? (paneType == "web" ? "Browser" : "Terminal") : req!.Name;
    // Auto-number to avoid duplicate surface names (matching cmux2).
    var usedNames = new HashSet<string>(ws.Surfaces.Select(s => s.Name));
    var name = baseName;
    for (int i = 2; usedNames.Contains(name); i++)
        name = $"{baseName} {i}";
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
app.MapPut("/api/settings", (CmuxSettings settings) =>
{
    SettingsService.Save(settings);
    return Results.Json(SettingsService.Current, json);
});
app.MapGet("/api/shells", () => Results.Json(Cmux.Core.Services.ShellDetector.DetectShells(), json));
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
app.MapGet("/api/notifications", (Cmux.Core.Services.NotificationService svc) =>
    Results.Json(new { items = svc.Notifications.ToArray(), unread = svc.UnreadCount }, json));
app.MapPost("/api/notifications", (Cmux.Core.Services.NotificationService svc, NotifyReq req) =>
{
    svc.AddNotification(req.WorkspaceId ?? "", req.SurfaceId ?? "", req.PaneId,
        req.Title ?? "Terminal", req.Subtitle, req.Body ?? "", Cmux.Core.Models.NotificationSource.Cli);
    return Results.Ok();
});
app.MapPost("/api/notifications/{id}/read", (Cmux.Core.Services.NotificationService svc, string id) =>
{ svc.MarkAsRead(id); return Results.Ok(); });
app.MapPost("/api/notifications/read-all", (Cmux.Core.Services.NotificationService svc) =>
{ svc.MarkAllAsRead(); return Results.Ok(); });
app.MapDelete("/api/notifications", (Cmux.Core.Services.NotificationService svc) =>
{ svc.Clear(); return Results.Ok(); });

// ── Command logs / history / transcripts ────────────────────────────
app.MapGet("/api/logs/dates", (Cmux.Core.Services.CommandLogService svc) =>
    Results.Json(svc.GetAvailableDates().Select(d => d.ToString("yyyy-MM-dd")), json));
app.MapGet("/api/logs", (Cmux.Core.Services.CommandLogService svc, string? date, string? q) =>
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

app.MapGet("/api/transcripts", (Cmux.Core.Services.CommandLogService svc) =>
    Results.Json(svc.GetTerminalTranscripts(), json));
app.MapGet("/api/transcripts/content", (Cmux.Core.Services.CommandLogService svc, string path) =>
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
app.MapGet("/api/snippets", (Cmux.Core.Services.SnippetService svc, string? q) =>
    Results.Json(svc.Search(q ?? ""), json));
app.MapGet("/api/snippets/categories", (Cmux.Core.Services.SnippetService svc) =>
    Results.Json(svc.GetCategories(), json));
app.MapPost("/api/snippets", (Cmux.Core.Services.SnippetService svc, Cmux.Core.Models.Snippet snippet) =>
{ svc.Add(snippet); return Results.Json(snippet, json); });
app.MapPut("/api/snippets/{id}", (Cmux.Core.Services.SnippetService svc, string id, Cmux.Core.Models.Snippet snippet) =>
{ snippet.Id = id; svc.Update(snippet); return Results.Json(snippet, json); });
app.MapDelete("/api/snippets/{id}", (Cmux.Core.Services.SnippetService svc, string id) =>
{ svc.Delete(id); return Results.Ok(); });
app.MapPost("/api/snippets/{id}/use", (Cmux.Core.Services.SnippetService svc, string id) =>
{ svc.IncrementUseCount(id); return Results.Ok(); });

// ── Workspace templates ─────────────────────────────────────────────
app.MapGet("/api/templates", (Cmux.Core.Services.WorkspaceTemplateService svc) =>
    Results.Json(svc.GetTemplates(), json));
app.MapPost("/api/templates", (Cmux.Core.Services.WorkspaceTemplateService svc, Cmux.Core.Services.WorkspaceTemplate t) =>
{ svc.Save(t); return Results.Json(t, json); });
app.MapPost("/api/templates/from-workspace/{wsId}", (
    AppStateStore store, Cmux.Core.Services.WorkspaceTemplateService svc, string wsId, RenameReq req) =>
{
    var ws = store.FindWorkspace(wsId);
    if (ws == null) return Results.NotFound();
    var template = new Cmux.Core.Services.WorkspaceTemplate
    {
        Name = string.IsNullOrWhiteSpace(req?.Name) ? ws.Name : req!.Name,
        EnvironmentVariables = new Dictionary<string, string>(ws.EnvironmentVariables),
    };
    foreach (var surface in ws.Surfaces)
    {
        var ts = new Cmux.Core.Services.TemplateSurface { Name = surface.Name };
        foreach (var pane in SplitTreeOps.AllPanes(surface.Root))
        {
            surface.Panes.TryGetValue(pane, out var paneDto);
            ts.Panes.Add(new Cmux.Core.Services.TemplatePaneLayout
            {
                WorkingDirectory = paneDto?.WorkingDirectory,
            });
        }
        template.Surfaces.Add(ts);
    }
    svc.Save(template);
    return Results.Json(template, json);
});
app.MapDelete("/api/templates/{id}", (Cmux.Core.Services.WorkspaceTemplateService svc, string id) =>
{ svc.Delete(id); return Results.Ok(); });
app.MapPost("/api/templates/{id}/apply", (AppStateStore store, Cmux.Core.Services.WorkspaceTemplateService svc, string id) =>
{
    var template = svc.GetTemplates().FirstOrDefault(t => t.Id == id);
    if (template == null) return Results.NotFound();

    SurfaceDto BuildSurface(Cmux.Core.Services.TemplateSurface ts)
    {
        var panes = ts.Panes.Count > 0 ? ts.Panes : new List<Cmux.Core.Services.TemplatePaneLayout> { new() };
        var paneDtos = panes.Select(p => new PaneDto { Type = "terminal", WorkingDirectory = p.WorkingDirectory }).ToList();
        // Build a left-leaning split tree from the pane list.
        SplitNodeDto root = new() { IsLeaf = true, PaneId = paneDtos[0].Id };
        for (int i = 1; i < paneDtos.Count; i++)
        {
            root = new SplitNodeDto
            {
                IsLeaf = false,
                Direction = panes[i].Direction == Cmux.Core.Models.SplitDirection.Horizontal ? "horizontal" : "vertical",
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
        : new List<Cmux.Core.Services.TemplateSurface> { new() })
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
app.MapGet("/api/quota", (Cmux.Core.Services.AgentQuotaService svc) =>
{
    var snap = svc.GetSnapshot();
    var windows = snap.RowsByWindow.ToDictionary(
        kv => kv.Key.ToString(),
        kv => new { rows = kv.Value, totalTokens = snap.TotalTokensFor(kv.Key), requests = snap.RequestsFor(kv.Key) });
    return Results.Json(new { generatedAtUtc = snap.GeneratedAtUtc, windows }, json);
});

// ── Git ─────────────────────────────────────────────────────────────
app.MapGet("/api/git/branch", (string? cwd) =>
    Results.Json(new { branch = Cmux.Core.Services.GitService.GetBranch(cwd), remote = Cmux.Core.Services.GitService.GetRemoteUrl(cwd) }, json));

// ── Ports ───────────────────────────────────────────────────────────
app.MapGet("/api/ports", (TerminalSessionManager term, string paneId) =>
{
    var session = term.Get(paneId);
    var pid = session?.ProcessId;
    return Results.Json(pid is int p ? Cmux.Core.Services.PortScanner.GetListeningPorts(p) : new List<int>(), json);
});
// ── External agents ─────────────────────────────────────────────────
app.MapGet("/api/agents", () =>
{
    var svc = new Cmux.Core.Services.ExternalAgentService();
    return Results.Json(svc.DetectAgents(), json);
});
app.MapGet("/api/agents/conversation", (string sessionFilePath, int? max) =>
{
    var svc = new Cmux.Core.Services.ExternalAgentService();
    var agent = new Cmux.Core.Models.ExternalAgentInfo { SessionFilePath = sessionFilePath };
    return Results.Json(svc.GetConversation(agent, max ?? 50), json);
});

// ── Agent conversation threads ──────────────────────────────────────
app.MapGet("/api/threads", (Cmux.Core.Services.AgentConversationStoreService svc) =>
    Results.Json(svc.GetAllThreads(), json));
app.MapPost("/api/threads", (Cmux.Core.Services.AgentConversationStoreService svc, AgentRuntimeService agent, AppStateStore store, AgentThreadCreateReq req) =>
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
app.MapGet("/api/threads/{id}/messages", (Cmux.Core.Services.AgentConversationStoreService svc, string id) =>
    Results.Json(svc.GetMessages(id), json));
app.MapDelete("/api/threads/{threadId}/messages/{messageId}", (Cmux.Core.Services.AgentConversationStoreService svc, string threadId, string messageId) =>
    svc.DeleteMessage(threadId, messageId) ? Results.Ok() : Results.NotFound());
app.MapDelete("/api/threads/{id}", (Cmux.Core.Services.AgentConversationStoreService svc, string id) =>
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

// ── Knowledge graph ─────────────────────────────────────────────────
app.MapGet("/api/knowledge-graph", async (Cmux.Core.Services.KnowledgeGraphService svc, string cwd) =>
{
    var root = Cmux.Core.Services.GitNexusService.ResolveRepoRoot(cwd) ?? cwd;
    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        return Results.Json(new { nodes = Array.Empty<object>(), edges = Array.Empty<object>() }, json);
    var snap = await svc.BuildFileTreeSnapshotAsync(root);
    return Results.Json(new { repoRoot = snap.RepoRoot, nodes = snap.Nodes, edges = snap.Edges }, json);
});
// ── Source tree (directory listing) ─────────────────────────────────
app.MapGet("/api/files", (string path) =>
{
    if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        return Results.Json(new { path, entries = Array.Empty<object>() }, json);
    try
    {
        var dir = new DirectoryInfo(path);
        var entries = dir.EnumerateFileSystemInfos()
            .Where(e => !e.Name.StartsWith('.') || e.Name is ".gitignore" or ".env")
            .OrderByDescending(e => (e.Attributes & FileAttributes.Directory) != 0)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new
            {
                name = e.Name,
                fullPath = e.FullName,
                isDirectory = (e.Attributes & FileAttributes.Directory) != 0,
                size = e is FileInfo fi ? fi.Length : 0L,
            })
            .ToArray();
        return Results.Json(new { path = dir.FullName, parent = dir.Parent?.FullName, entries }, json);
    }
    catch (Exception ex) { return Results.Json(new { path, error = ex.Message, entries = Array.Empty<object>() }, json); }
});
app.MapGet("/api/files/content", (string path) =>
{
    if (!File.Exists(path)) return Results.NotFound();
    try
    {
        var info = new FileInfo(path);
        if (info.Length > 2 * 1024 * 1024) return Results.Text($"<file too large: {info.Length} bytes>");
        return Results.Text(File.ReadAllText(path));
    }
    catch (Exception ex) { return Results.Text($"<error: {ex.Message}>"); }
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
    var ranked = Cmux.Core.Services.FuzzyMatcher
        .RankMatches(rel, q, x => x.name)
        .Take(200)
        .Select(r => r.Item);
    return Results.Json(ranked, json);
});
// ── Per-workspace status (git branch + unread) ─────────────────────
app.MapGet("/api/workspaces/status", (AppStateStore store, Cmux.Core.Services.NotificationService notif) =>
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
            branch = Cmux.Core.Services.GitService.GetBranch(cwd),
            unread = notif.GetUnreadCount(w.Id),
        };
    });
    return Results.Json(result, json);
});
// ── Agent runtime (chat with AI agent, streaming over SSE) ──────────
app.MapGet("/api/agent/settings", () => Results.Json(SettingsService.Current.Agent, json));
app.MapPut("/api/agent/settings", (Cmux.Core.Config.AgentSettings agent) =>
{
    var s = SettingsService.Current;
    s.Agent = agent;
    SettingsService.Save(s);
    return Results.Json(SettingsService.Current.Agent, json);
});
app.MapPut("/api/agent/secret", (AgentSecretReq req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest();
    Cmux.Core.Services.SecretStoreService.SetSecret(req.Name, req.Value);
    return Results.Ok();
});

app.MapPost("/api/agent/send", (AgentRuntimeService agent, TerminalSessionManager term, AppStateStore store, AgentSendReq req) =>
{
    var ctx = store.FindPaneContext(req.PaneId);
    var pane = ctx?.Pane;
    var context = new Cmux.Web.Services.AgentPaneContext
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

// Bridge the agent runtime "cmux" tool to a minimal command surface.
{
    var agentRuntime = app.Services.GetRequiredService<AgentRuntimeService>();
    var store = app.Services.GetRequiredService<AppStateStore>();
    var term = app.Services.GetRequiredService<TerminalSessionManager>();
    var notif = app.Services.GetRequiredService<Cmux.Core.Services.NotificationService>();
    agentRuntime.CommandHandler = (command, args) =>
    {
        string Resp(object o) => JsonSerializer.Serialize(o, json);
        var ws = store.State.Workspaces.FirstOrDefault(w => w.Id == store.State.SelectedWorkspaceId)
                 ?? store.State.Workspaces.FirstOrDefault();
        var surface = ws?.Surfaces.FirstOrDefault(s => s.Id == ws.SelectedSurfaceId) ?? ws?.Surfaces.FirstOrDefault();
        switch (command)
        {
            case "STATUS":
                return Task.FromResult(Resp(new { workspaces = store.State.Workspaces.Count, selected = ws?.Name }));
            case "WORKSPACE.LIST":
                return Task.FromResult(Resp(store.State.Workspaces.Select(w => new { id = w.Id, name = w.Name })));
            case "NOTIFY":
                notif.AddNotification(ws?.Id ?? "", surface?.Id ?? "", null,
                    args.GetValueOrDefault("title", "Agent"), args.GetValueOrDefault("subtitle"),
                    args.GetValueOrDefault("body", ""), Cmux.Core.Models.NotificationSource.Cli);
                return Task.FromResult(Resp(new { ok = true }));
            case "PANE.LIST":
                return Task.FromResult(Resp(surface?.Panes.Values.Select(p => new { id = p.Id, type = p.Type, cwd = p.WorkingDirectory }) ?? []));
            case "PANE.READ":
            {
                var paneId = args.GetValueOrDefault("pane", surface?.FocusedPaneId ?? "");
                var session = term.Get(paneId);
                return Task.FromResult(Resp(new { pane = paneId, content = session?.Buffer.ExportPlainText() ?? "" }));
            }
            case "PANE.WRITE":
            {
                var paneId = args.GetValueOrDefault("pane", surface?.FocusedPaneId ?? "");
                var text = args.GetValueOrDefault("text", "");
                term.Write(paneId, System.Text.Encoding.UTF8.GetBytes(text));
                return Task.FromResult(Resp(new { ok = true }));
            }
            default:
                return Task.FromResult(Resp(new { error = $"Unsupported command: {command}" }));
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
record AgentSendReq(string PaneId, string Prompt, string? ThreadId);

public partial class Program
{
    static SplitNodeDto? FindNodeById(SplitNodeDto node, string id)
    {
        if (node.Id == id) return node;
        if (node.IsLeaf) return null;
        return (node.First != null ? FindNodeById(node.First, id) : null)
            ?? (node.Second != null ? FindNodeById(node.Second, id) : null);
    }
}























