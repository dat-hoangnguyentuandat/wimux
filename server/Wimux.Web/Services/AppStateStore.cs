using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wimux.Web.Services;

// ── DTOs mirroring the web client state tree ──────────────────────────

public class PaneDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Type { get; set; } = "terminal"; // terminal | web | notepad
    public string? Title { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Url { get; set; }
    public string? Notes { get; set; }
    public string? Shell { get; set; }
}

public class SplitNodeDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public bool IsLeaf { get; set; } = true;
    public string Direction { get; set; } = "vertical"; // vertical | horizontal
    public double SplitRatio { get; set; } = 0.5;
    public string? PaneId { get; set; }
    public SplitNodeDto? First { get; set; }
    public SplitNodeDto? Second { get; set; }
}

public class SurfaceDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Terminal";
    public SplitNodeDto Root { get; set; } = new();
    public string? FocusedPaneId { get; set; }
    public Dictionary<string, PaneDto> Panes { get; set; } = new();
}

public class WorkspaceDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Workspace";
    public string AccentColor { get; set; } = "#818CF8";
    public string? WorkingDirectory { get; set; }
    public List<SurfaceDto> Surfaces { get; set; } = new();
    public string? SelectedSurfaceId { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public List<SshProfileDto> SshProfiles { get; set; } = new();
}

public class SshProfileDto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string? IdentityFile { get; set; }
}

public class AppStateDto
{
    public int Version { get; set; } = 1;
    public List<WorkspaceDto> Workspaces { get; set; } = new();
    public string? SelectedWorkspaceId { get; set; }
}

/// <summary>
/// Holds the in-memory workspace/surface/split tree and persists it to disk.
/// Mirrors the desktop session-persistence concept for the web app.
/// </summary>
public sealed class AppStateStore
{
    private static string Dir =>
        Environment.GetEnvironmentVariable("WIMUX_STATE_DIR")
        ?? Environment.GetEnvironmentVariable("WIMUX3_STATE_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wimux");
    private static string FilePath => Path.Combine(Dir, "state.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _lock = new();
    public AppStateDto State { get; private set; }

    public AppStateStore()
    {
        State = Load();
        if (State.Workspaces.Count == 0)
            Seed();
    }

    private void Seed()
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
            Name = "Default",
            Surfaces = { surface },
            SelectedSurfaceId = surface.Id,
        };
        State.Workspaces.Add(ws);
        State.SelectedWorkspaceId = ws.Id;
        Save();
    }

    private static AppStateDto Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var state = JsonSerializer.Deserialize<AppStateDto>(json, JsonOpts);
                if (state != null) return state;
            }
        }
        catch { /* fall through to fresh state */ }
        return new AppStateDto();
    }

    public void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(State, JsonOpts);
            File.WriteAllText(FilePath, json);
        }
    }

    public void Mutate(Action<AppStateDto> mutator)
    {
        lock (_lock)
        {
            mutator(State);
        }
        Save();
    }

    public WorkspaceDto? FindWorkspace(string id) => State.Workspaces.FirstOrDefault(w => w.Id == id);

    public SurfaceDto? FindSurface(string workspaceId, string surfaceId) =>
        FindWorkspace(workspaceId)?.Surfaces.FirstOrDefault(s => s.Id == surfaceId);

    /// <summary>
    /// Resolves the workspace/surface a pane belongs to by scanning every surface.
    /// Used to attribute terminal notifications and command-log entries.
    /// </summary>
    public PaneContext? FindPaneContext(string paneId)
    {
        lock (_lock)
        {
            foreach (var ws in State.Workspaces)
                foreach (var surface in ws.Surfaces)
                    if (surface.Panes.TryGetValue(paneId, out var pane))
                        return new PaneContext(ws, surface, pane);
        }
        return null;
    }
}

public sealed record PaneContext(WorkspaceDto Workspace, SurfaceDto Surface, PaneDto Pane);



