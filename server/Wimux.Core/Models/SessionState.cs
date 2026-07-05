using System.Text.Json.Serialization;

namespace Wimux.Core.Models;

public class SessionState
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("workspaces")]
    public List<WorkspaceState> Workspaces { get; set; } = [];

    [JsonPropertyName("selectedWorkspaceIndex")]
    public int? SelectedWorkspaceIndex { get; set; }

    [JsonPropertyName("window")]
    public WindowState? Window { get; set; }
}

public class WorkspaceState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("iconGlyph")]
    public string? IconGlyph { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("surfaces")]
    public List<SurfaceState> Surfaces { get; set; } = [];

    [JsonPropertyName("selectedSurfaceIndex")]
    public int? SelectedSurfaceIndex { get; set; }
}

public class SurfaceState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("rootNode")]
    public SplitNodeState? RootNode { get; set; }

    [JsonPropertyName("focusedPaneId")]
    public string? FocusedPaneId { get; set; }

    [JsonPropertyName("paneCustomNames")]
    public Dictionary<string, string> PaneCustomNames { get; set; } = [];

    [JsonPropertyName("paneSnapshots")]
    public Dictionary<string, PaneStateSnapshot> PaneSnapshots { get; set; } = [];
}

public class SplitNodeState
{
    [JsonPropertyName("isLeaf")]
    public bool IsLeaf { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "Vertical";

    [JsonPropertyName("splitRatio")]
    public double SplitRatio { get; set; } = 0.5;

    [JsonPropertyName("paneId")]
    public string? PaneId { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("first")]
    public SplitNodeState? First { get; set; }

    [JsonPropertyName("second")]
    public SplitNodeState? Second { get; set; }
}

public class WindowState
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("width")]
    public double Width { get; set; }

    [JsonPropertyName("height")]
    public double Height { get; set; }

    [JsonPropertyName("isMaximized")]
    public bool IsMaximized { get; set; }

    [JsonPropertyName("sidebarWidth")]
    public double SidebarWidth { get; set; } = 280;

    [JsonPropertyName("sidebarVisible")]
    public bool SidebarVisible { get; set; } = true;

    [JsonPropertyName("agentChatVisible")]
    public bool AgentChatVisible { get; set; } = true;

    [JsonPropertyName("terminalVisible")]
    public bool TerminalVisible { get; set; } = true;

    [JsonPropertyName("compactSidebar")]
    public bool CompactSidebar { get; set; }

    [JsonPropertyName("toolPanels")]
    public List<ToolPanelLayout> ToolPanels { get; set; } = [];

    [JsonPropertyName("dockGroups")]
    public List<DockGroupSize> DockGroups { get; set; } = [];
}

public class ToolPanelLayout
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "closed";   // docked | floating | parked | closed

    [JsonPropertyName("floatLeft")]
    public double FloatLeft { get; set; }

    [JsonPropertyName("floatTop")]
    public double FloatTop { get; set; }

    [JsonPropertyName("floatWidth")]
    public double FloatWidth { get; set; }

    [JsonPropertyName("floatHeight")]
    public double FloatHeight { get; set; }
}

public class DockGroupSize
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";          // Sidebar | Terminal | AgentChat | Tools

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("star")]
    public bool Star { get; set; }
}
