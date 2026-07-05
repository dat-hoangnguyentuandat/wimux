namespace Wimux.Web.Services;

/// <summary>
/// Pure operations on the split-tree: split a leaf, remove a pane and collapse.
/// </summary>
public static class SplitTreeOps
{
    public static SplitNodeDto? Find(SplitNodeDto node, string paneId)
    {
        if (node.IsLeaf) return node.PaneId == paneId ? node : null;
        return (node.First != null ? Find(node.First, paneId) : null)
            ?? (node.Second != null ? Find(node.Second, paneId) : null);
    }

    /// <summary>Splits the leaf holding paneId. Returns the newly created pane id, or null.</summary>
    public static string? Split(SurfaceDto surface, string paneId, string direction, PaneDto newPane)
    {
        var leaf = Find(surface.Root, paneId);
        if (leaf == null || !leaf.IsLeaf) return null;

        var first = new SplitNodeDto { IsLeaf = true, PaneId = leaf.PaneId };
        var second = new SplitNodeDto { IsLeaf = true, PaneId = newPane.Id };

        leaf.IsLeaf = false;
        leaf.Direction = direction == "horizontal" ? "horizontal" : "vertical";
        leaf.PaneId = null;
        leaf.SplitRatio = 0.5;
        leaf.First = first;
        leaf.Second = second;

        surface.Panes[newPane.Id] = newPane;
        return newPane.Id;
    }

    /// <summary>Removes a pane and collapses its parent. Returns a sibling pane id to focus, or null.</summary>
    public static string? RemovePane(SurfaceDto surface, string paneId)
    {
        if (surface.Root.IsLeaf && surface.Root.PaneId == paneId)
        {
            surface.Panes.Remove(paneId);
            return null; // surface becomes empty
        }

        var parent = FindParent(surface.Root, paneId);
        if (parent == null) return null;

        var keep = (parent.First!.IsLeaf && parent.First.PaneId == paneId) ? parent.Second! : parent.First!;

        parent.IsLeaf = keep.IsLeaf;
        parent.Direction = keep.Direction;
        parent.SplitRatio = keep.SplitRatio;
        parent.PaneId = keep.PaneId;
        parent.First = keep.First;
        parent.Second = keep.Second;

        surface.Panes.Remove(paneId);
        return FirstLeafPane(parent);
    }

    private static SplitNodeDto? FindParent(SplitNodeDto node, string paneId)
    {
        if (node.IsLeaf) return null;
        if ((node.First!.IsLeaf && node.First.PaneId == paneId) ||
            (node.Second!.IsLeaf && node.Second.PaneId == paneId))
            return node;
        return FindParent(node.First, paneId) ?? FindParent(node.Second, paneId);
    }

    public static string? FirstLeafPane(SplitNodeDto node)
    {
        if (node.IsLeaf) return node.PaneId;
        return (node.First != null ? FirstLeafPane(node.First) : null)
            ?? (node.Second != null ? FirstLeafPane(node.Second) : null);
    }

    public static IEnumerable<string> AllPanes(SplitNodeDto node)
    {
        if (node.IsLeaf)
        {
            if (node.PaneId != null) yield return node.PaneId;
            yield break;
        }
        if (node.First != null) foreach (var p in AllPanes(node.First)) yield return p;
        if (node.Second != null) foreach (var p in AllPanes(node.Second)) yield return p;
    }
}
