using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Wimux.Launcher;

internal static class RunLauncher
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    internal static int Open(string[] args)
    {
        var parsed = ParseRunArgs(args);
        if (parsed.Command.Count == 0)
        {
            Console.Error.WriteLine("Usage: wimux run [--name <tab>] [--cwd <dir>] -- <command> [args]");
            return 1;
        }

        return OpenCommand(parsed.Name ?? GuessName(parsed.Command), parsed.Cwd, BuildCommandLine(parsed.Command));
    }

    internal static int OpenWindowsTerminal(string[] args)
    {
        var parsed = ParseWindowsTerminalArgs(args);
        if (parsed.Command.Count == 0)
        {
            parsed.Command.Add(ResolvePowerShellExecutable());
        }

        return OpenCommand(parsed.Name ?? GuessName(parsed.Command), parsed.Cwd ?? Environment.CurrentDirectory, BuildCommandLine(parsed.Command));
    }

    private static int OpenCommand(string name, string? cwd, string command)
    {
        ServerManager.EnsureRunning();
        var url = ServerManager.Url;
        if (!WaitForServer(url))
        {
            Console.Error.WriteLine($"wimux server is not reachable at {url}.");
            return 1;
        }

        if (!TryOpenSurface(url, name, cwd, command, out var surfaceId, out var paneId, out var error))
        {
            Console.Error.WriteLine($"Failed to open terminal in wimux: {error}");
            return 1;
        }

        var target = $"{url}/#surface={surfaceId}&pane={paneId}";
        BrowserLauncher.Open(target);
        Console.WriteLine($"Opened {name} in wimux ({paneId}).");
        Console.WriteLine($"If the browser did not open automatically, visit: {target}");
        return 0;
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
            catch { }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool TryOpenSurface(string url, string name, string? cwd, string command, out string surfaceId, out string paneId, out string error)
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
                name,
                shell = command,
                workingDirectory = string.IsNullOrWhiteSpace(cwd) ? null : cwd,
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

    private static (string? Name, string? Cwd, List<string> Command) ParseRunArgs(string[] args)
    {
        string? name = null;
        string? cwd = null;
        var command = new List<string>();
        var passthrough = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!passthrough && arg == "--")
            {
                passthrough = true;
                continue;
            }

            if (!passthrough && (arg == "--name" || arg == "-n") && i + 1 < args.Length)
                name = args[++i];
            else if (!passthrough && (arg == "--cwd" || arg == "-d") && i + 1 < args.Length)
                cwd = args[++i];
            else
                command.Add(arg);
        }

        return (name, cwd, command);
    }

    private static (string? Name, string? Cwd, List<string> Command) ParseWindowsTerminalArgs(string[] args)
    {
        string? name = null;
        string? cwd = null;
        var command = new List<string>();
        var readingCommand = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var lower = arg.ToLowerInvariant();

            if (!readingCommand && (lower == "new-tab" || lower == "nt" || lower == "split-pane" || lower == "sp"))
                continue;
            if (!readingCommand && (lower == "-d" || lower == "--startingdirectory") && i + 1 < args.Length)
            {
                cwd = args[++i];
                continue;
            }
            if (!readingCommand && (lower == "--title" || lower == "--tabcolor" || lower == "-p" || lower == "--profile") && i + 1 < args.Length)
            {
                var value = args[++i];
                if (lower == "--title") name = value;
                continue;
            }
            if (!readingCommand && (lower == "-w" || lower == "--window") && i + 1 < args.Length)
            {
                i++;
                continue;
            }
            if (!readingCommand && arg == "--")
            {
                readingCommand = true;
                continue;
            }

            readingCommand = true;
            command.Add(arg);
        }

        return (name, cwd, command);
    }

    private static string GuessName(IReadOnlyList<string> command)
    {
        if (command.Count == 0) return "Terminal";
        var exe = Path.GetFileNameWithoutExtension(command[0].Trim('"'));
        return string.IsNullOrWhiteSpace(exe) ? "Terminal" : exe;
    }

    private static string BuildCommandLine(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteArg(arg));
        }
        return sb.ToString();
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        if (arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        return "\"" + arg.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string? ResolveExecutableOutsideLauncherDir(string exeName)
    {
        var launcherDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var rawDir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir;
            try { dir = Path.GetFullPath(rawDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch { continue; }

            if (string.Equals(dir, launcherDir, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string ResolvePowerShellExecutable()
    {
        foreach (var candidate in GetPowerShellCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (Path.IsPathFullyQualified(candidate))
            {
                if (File.Exists(candidate))
                    return candidate;
                continue;
            }

            var resolved = ResolveExecutableOutsideLauncherDir(candidate);
            if (resolved != null)
                return resolved;
        }

        var comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec))
            return comspec;

        return "cmd.exe";
    }

    private static IEnumerable<string> GetPowerShellCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        yield return "pwsh.exe";

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrWhiteSpace(system))
            yield return Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            yield return Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            yield return Path.Combine(windows, "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
        }

        yield return "powershell.exe";
    }
}
