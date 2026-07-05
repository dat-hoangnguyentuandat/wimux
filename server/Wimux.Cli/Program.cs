using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Wimux.Cli;

/// <summary>
/// wimux CLI — talks to the running wimux web host over HTTP/REST.
/// The base URL defaults to http://localhost:5201 and can be overridden
/// with the WIMUX_URL environment variable.
///
/// Usage:
///   wimux notify --title "Title" --body "Body"
///   wimux workspace list
///   wimux workspace create --name "My Workspace"
///   wimux workspace select --index 0
///   wimux surface create
///   wimux split right
///   wimux split down
///   wimux status
/// </summary>
public static class Program
{
    private static readonly string BaseUrl =
        (Environment.GetEnvironmentVariable("WIMUX_URL") ?? "http://localhost:5201").TrimEnd('/');

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { PrintHelp(); return 0; }
        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "notify" => await HandleNotify(args[1..]),
                "workspace" => await HandleWorkspace(args[1..]),
                "surface" => await HandleSurface(args[1..]),
                "split" => await HandleSplit(args[1..]),
                "status" => await HandleStatus(),
                "help" or "--help" or "-h" => PrintHelp(),
                "version" or "--version" or "-v" => PrintVersion(),
                _ => Error($"Unknown command: {command}"),
            };
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine($"Error: Could not connect to wimux at {BaseUrl}. Is it running?");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine($"Error: Request to wimux at {BaseUrl} timed out.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<JsonElement> GetState()
    {
        var json = await Http.GetStringAsync($"{BaseUrl}/api/state");
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static async Task<int> HandleNotify(string[] args)
    {
        var parsed = ParseArgs(args);
        var title = parsed.GetValueOrDefault("title", parsed.GetValueOrDefault("_arg0", "Terminal"));
        var body = parsed.GetValueOrDefault("body", parsed.GetValueOrDefault("_arg1", ""));
        var subtitle = parsed.GetValueOrDefault("subtitle");

        var state = await GetState();
        string workspaceId = "", surfaceId = "";
        if (state.TryGetProperty("selectedWorkspaceId", out var wsId) && wsId.ValueKind == JsonValueKind.String)
            workspaceId = wsId.GetString() ?? "";
        var ws = FindSelectedWorkspace(state);
        if (ws is { } w && w.TryGetProperty("selectedSurfaceId", out var sId) && sId.ValueKind == JsonValueKind.String)
            surfaceId = sId.GetString() ?? "";

        var resp = await Http.PostAsJsonAsync($"{BaseUrl}/api/notifications", new
        {
            workspaceId,
            surfaceId,
            title,
            subtitle,
            body,
        });
        resp.EnsureSuccessStatusCode();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true }, Json));
        return 0;
    }

    private static async Task<int> HandleWorkspace(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: wimux workspace <list|create|select|next|previous>"); return 1; }
        var sub = args[0].ToLowerInvariant();
        var parsed = ParseArgs(args[1..]);

        switch (sub)
        {
            case "list" or "ls":
            {
                var state = await GetState();
                var selectedId = state.TryGetProperty("selectedWorkspaceId", out var s) ? s.GetString() : null;
                var list = state.GetProperty("workspaces").EnumerateArray().Select(w => new
                {
                    id = w.GetProperty("id").GetString(),
                    name = w.GetProperty("name").GetString(),
                    selected = w.GetProperty("id").GetString() == selectedId,
                    surfaces = w.GetProperty("surfaces").GetArrayLength(),
                });
                Console.WriteLine(JsonSerializer.Serialize(list, Json));
                return 0;
            }
            case "create" or "new":
            {
                var name = parsed.GetValueOrDefault("name", parsed.GetValueOrDefault("_arg0", "Workspace"));
                var resp = await Http.PostAsJsonAsync($"{BaseUrl}/api/workspaces", new { name });
                resp.EnsureSuccessStatusCode();
                Console.WriteLine(await resp.Content.ReadAsStringAsync());
                return 0;
            }
            case "select":
            {
                var id = await ResolveWorkspaceId(parsed);
                if (id == null) return Error("Workspace not found.");
                var resp = await Http.PostAsync($"{BaseUrl}/api/workspaces/{id}/select", null);
                resp.EnsureSuccessStatusCode();
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, id }, Json));
                return 0;
            }
            case "next" or "previous" or "prev":
                return await CycleWorkspace(sub != "next" ? -1 : 1);
            default:
                return Error($"Unknown workspace command: {sub}");
        }
    }

    private static async Task<int> CycleWorkspace(int dir)
    {
        var state = await GetState();
        var ids = state.GetProperty("workspaces").EnumerateArray()
            .Select(w => w.GetProperty("id").GetString()!).ToList();
        if (ids.Count == 0) return Error("No workspaces.");
        var selectedId = state.TryGetProperty("selectedWorkspaceId", out var s) ? s.GetString() : ids[0];
        var i = Math.Max(0, ids.IndexOf(selectedId!));
        var next = ids[(i + dir + ids.Count) % ids.Count];
        var resp = await Http.PostAsync($"{BaseUrl}/api/workspaces/{next}/select", null);
        resp.EnsureSuccessStatusCode();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, id = next }, Json));
        return 0;
    }

    private static async Task<string?> ResolveWorkspaceId(Dictionary<string, string> parsed)
    {
        if (parsed.TryGetValue("id", out var id)) return id;
        var state = await GetState();
        var ids = state.GetProperty("workspaces").EnumerateArray()
            .Select(w => w.GetProperty("id").GetString()!).ToList();
        parsed.TryGetValue("index", out var idxStr);
        if (idxStr == null) parsed.TryGetValue("_arg0", out idxStr);
        if (int.TryParse(idxStr, out var idx) && idx >= 0 && idx < ids.Count) return ids[idx];
        return null;
    }

    private static async Task<int> HandleSurface(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: wimux surface <create|next|previous>"); return 1; }
        var sub = args[0].ToLowerInvariant();
        var state = await GetState();
        var ws = FindSelectedWorkspace(state);
        if (ws is not { } w) return Error("No selected workspace.");
        var wsId = w.GetProperty("id").GetString();

        switch (sub)
        {
            case "create" or "new":
            {
                var resp = await Http.PostAsJsonAsync($"{BaseUrl}/api/workspaces/{wsId}/surfaces", new { name = (string?)null });
                resp.EnsureSuccessStatusCode();
                Console.WriteLine(await resp.Content.ReadAsStringAsync());
                return 0;
            }
            case "next" or "previous" or "prev":
            {
                var surfaces = w.GetProperty("surfaces").EnumerateArray()
                    .Select(su => su.GetProperty("id").GetString()!).ToList();
                if (surfaces.Count == 0) return Error("No surfaces.");
                var selId = w.TryGetProperty("selectedSurfaceId", out var sp) ? sp.GetString() : surfaces[0];
                var i = Math.Max(0, surfaces.IndexOf(selId!));
                var dir = sub == "next" ? 1 : -1;
                var next = surfaces[(i + dir + surfaces.Count) % surfaces.Count];
                var resp = await Http.PostAsync($"{BaseUrl}/api/workspaces/{wsId}/surfaces/{next}/select", null);
                resp.EnsureSuccessStatusCode();
                Console.WriteLine(JsonSerializer.Serialize(new { ok = true, id = next }, Json));
                return 0;
            }
            default:
                return Error($"Unknown surface command: {sub}");
        }
    }

    private static async Task<int> HandleSplit(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("Usage: wimux split <right|down>"); return 1; }
        var direction = args[0].ToLowerInvariant() switch
        {
            "right" or "vertical" or "v" => "vertical",
            "down" or "horizontal" or "h" => "horizontal",
            _ => null,
        };
        if (direction == null) return Error($"Unknown split direction: {args[0]}");

        var state = await GetState();
        var ws = FindSelectedWorkspace(state);
        if (ws is not { } w) return Error("No selected workspace.");
        var wsId = w.GetProperty("id").GetString();
        var surface = FindSelectedSurface(w);
        if (surface is not { } su) return Error("No selected surface.");
        var sId = su.GetProperty("id").GetString();
        var paneId = su.TryGetProperty("focusedPaneId", out var fp) && fp.ValueKind == JsonValueKind.String
            ? fp.GetString()
            : su.GetProperty("panes").EnumerateObject().Select(p => p.Name).FirstOrDefault();
        if (paneId == null) return Error("No pane to split.");

        var resp = await Http.PostAsJsonAsync($"{BaseUrl}/api/workspaces/{wsId}/surfaces/{sId}/split",
            new { paneId, direction });
        resp.EnsureSuccessStatusCode();
        Console.WriteLine(JsonSerializer.Serialize(new { ok = true, direction }, Json));
        return 0;
    }

    private static async Task<int> HandleStatus()
    {
        var state = await GetState();
        var selectedId = state.TryGetProperty("selectedWorkspaceId", out var s) ? s.GetString() : null;
        var workspaces = state.GetProperty("workspaces");
        var ws = FindSelectedWorkspace(state);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            url = BaseUrl,
            workspaces = workspaces.GetArrayLength(),
            selectedWorkspace = ws is { } w ? w.GetProperty("name").GetString() : null,
            selectedWorkspaceId = selectedId,
            surfaces = ws is { } w2 ? w2.GetProperty("surfaces").GetArrayLength() : 0,
        }, Json));
        return 0;
    }

    private static JsonElement? FindSelectedWorkspace(JsonElement state)
    {
        var selectedId = state.TryGetProperty("selectedWorkspaceId", out var s) ? s.GetString() : null;
        JsonElement? first = null;
        foreach (var w in state.GetProperty("workspaces").EnumerateArray())
        {
            first ??= w;
            if (w.GetProperty("id").GetString() == selectedId) return w;
        }
        return first;
    }

    private static JsonElement? FindSelectedSurface(JsonElement workspace)
    {
        var selectedId = workspace.TryGetProperty("selectedSurfaceId", out var s) ? s.GetString() : null;
        JsonElement? first = null;
        foreach (var su in workspace.GetProperty("surfaces").EnumerateArray())
        {
            first ??= su;
            if (su.GetProperty("id").GetString() == selectedId) return su;
        }
        return first;
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var result = new Dictionary<string, string>();
        int positional = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--"))
            {
                var key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) { result[key] = args[++i]; }
                else result[key] = "true";
            }
            else if (arg.StartsWith('-') && arg.Length == 2)
            {
                var key = arg[1..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) { result[key] = args[++i]; }
                else result[key] = "true";
            }
            else { result[$"_arg{positional}"] = arg; positional++; }
        }
        return result;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            wimux - Terminal workspace CLI (wimux web host)

            Usage:
              wimux <command> [options]

            Environment:
              WIMUX_URL              Base URL of the host (default http://localhost:5201)

            Commands:
              notify                Send a notification
                --title <text>      Notification title (default: "Terminal")
                --body <text>       Notification body
                --subtitle <text>   Notification subtitle

              workspace             Manage workspaces
                list                List all workspaces
                create --name <t>   Create a new workspace
                select --index <n>  Select workspace by index (or --id <id>)
                next | previous     Cycle workspaces

              surface               Manage surfaces (tabs)
                create              Create a new surface
                next | previous     Cycle surfaces

              split <right|down>    Split the focused pane

              status                Show host status
            """);
        return 0;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("wimux 1.0.0 (web)");
        return 0;
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }
}

