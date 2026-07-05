using System.Collections.ObjectModel;

namespace Wimux.Core.Models;

public class Workspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Workspace";
    public string IconGlyph { get; set; } = "\uE8A5";
    public string AccentColor { get; set; } = "#FF818CF8";
    public ObservableCollection<Surface> Surfaces { get; set; } = [];
    public Surface? SelectedSurface { get; set; }
    public string? GitBranch { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? LinkedPrStatus { get; set; }
    public string? LinkedPrNumber { get; set; }
    public List<int> ListeningPorts { get; set; } = [];
    public string? LatestNotificationText { get; set; }
    public int UnreadNotificationCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Per-workspace environment variables injected into every new terminal session.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
    /// <summary>Workspace-level SSH profiles (name → host).</summary>
    public List<SshProfile> SshProfiles { get; set; } = [];
}

public class SshProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public string? IdentityFile { get; set; }
}
