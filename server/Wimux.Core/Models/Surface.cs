namespace Wimux.Core.Models;

public class Surface
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Terminal";
    public SplitNode RootSplitNode { get; set; } = SplitNode.CreateLeaf();
    public string? FocusedPaneId { get; set; }
    public Dictionary<string, string> PaneCustomNames { get; set; } = [];
    public Dictionary<string, PaneStateSnapshot> PaneSnapshots { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Optional tab accent color hex e.g. "#FF6366F1". Null = default.</summary>
    public string? TabColor { get; set; }
}
