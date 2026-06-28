using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Cmux.Launcher;

/// <summary>
/// Opens a cmux3 web-terminal pane whose command is `codex` so the user can
/// drive the codex CLI from the browser. Vietnamese typing in the web pane
/// is handled by the OS TSF IME (Unikey / EVKey / GoTiengViet) — xterm.js
/// forwards the committed rune into the cmux3 server which writes it
/// verbatim into the ConPTY, the same way a native Windows console would.
/// </summary>
internal static class CodexLauncher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    internal static int Open(string[] codexArgs)
    {
        var codexExe = ResolveCodexExecutable();
        if (codexExe == null)
        {
            Console.Error.WriteLine("Could not find the `codex` CLI on PATH or in the standard install location.");
            Console.Error.WriteLine("Install it with:  npm install -g @openai/codex");
            return 1;
        }

        var url = ServerManager.Url;
        if (!WaitForServer(url))
        {
            Console.Error.WriteLine($"cmux3 server is not reachable at {url}.");
            return 1;
        }

        var codexCommand = BuildCodexCommand(codexExe, codexArgs);
        if (!TryOpenCodexSurface(url, codexCommand, out var surfaceId, out var paneId, out var error))
        {
            Console.Error.WriteLine($"Failed to open a codex pane in cmux3: {error}");
            return 1;
        }

        var target = $"{url}/#surface={surfaceId}&pane={paneId}";
        BrowserLauncher.Open(target);
        Console.WriteLine($"Opened codex in cmux3 ({paneId}).");
        Console.WriteLine($"If the browser did not open automatically, visit: {target}");
        return 0;
    }

    private static string? ResolveCodexExecutable()
    {
        // 1) On PATH.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "codex.exe", "codex.cmd", "codex" })
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 2) npm global install on Windows.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var name in new[] { "codex.exe", "codex.cmd" })
        {
            var candidate = Path.Combine(appData, "npm", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string BuildCodexCommand(string codexExe, string[] codexArgs)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(codexExe.Replace("\"", "\\\"")).Append('"');
        foreach (var a in codexArgs)
        {
            sb.Append(' ');
            if (a.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0)
                sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
            else
                sb.Append(a);
        }
        return sb.ToString();
    }

    private static bool WaitForServer(string url)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var resp = Http.GetAsync($"{url}/api/state").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* not up yet */ }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool TryOpenCodexSurface(string url, string command, out string surfaceId, out string paneId, out string error)
    {
        surfaceId = "";
        paneId = "";
        error = "";

        try
        {
            using var stateResp = Http.GetAsync($"{url}/api/state").GetAwaiter().GetResult();
            stateResp.EnsureSuccessStatusCode();
            using var stateDoc = JsonDocument.Parse(stateResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            var root = stateDoc.RootElement;

            if (!root.TryGetProperty("workspaces", out var workspaces) || workspaces.GetArrayLength() == 0)
            {
                error = "no workspaces exist yet";
                return false;
            }

            var wsId = root.TryGetProperty("selectedWorkspaceId", out var sel) && sel.ValueKind == JsonValueKind.String
                ? sel.GetString()!
                : workspaces[0].GetProperty("id").GetString()!;

            var payload = JsonSerializer.Serialize(new
            {
                name = "Codex",
                shell = command,
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var createResp = Http.PostAsync($"{url}/api/workspaces/{wsId}/surfaces", content).GetAwaiter().GetResult();
            createResp.EnsureSuccessStatusCode();
            using var createDoc = JsonDocument.Parse(createResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            var surface = createDoc.RootElement;
            surfaceId = surface.GetProperty("id").GetString() ?? "";
            paneId = surface.TryGetProperty("focusedPaneId", out var fp) && fp.ValueKind == JsonValueKind.String
                ? fp.GetString() ?? ""
                : "";

            if (string.IsNullOrEmpty(paneId) && surface.TryGetProperty("panes", out var panes))
            {
                foreach (var p in panes.EnumerateObject()) { paneId = p.Name; break; }
            }
            return !string.IsNullOrEmpty(surfaceId) && !string.IsNullOrEmpty(paneId);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
