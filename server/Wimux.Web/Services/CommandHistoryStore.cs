using System.Collections.Concurrent;

namespace Wimux.Web.Services;

/// <summary>
/// In-memory per-pane shell command history, mirroring the desktop
/// SurfaceViewModel command-history feature. Backed by prompt markers.
/// </summary>
public sealed class CommandHistoryStore
{
    private readonly ConcurrentDictionary<string, List<string>> _byPane = new();
    private readonly object _lock = new();

    public void Append(string paneId, string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        lock (_lock)
        {
            var list = _byPane.GetOrAdd(paneId, _ => new List<string>());
            if (list.Count > 0 && list[^1] == command) return;
            list.Add(command);
            if (list.Count > 500) list.RemoveRange(0, list.Count - 500);
        }
    }

    public IReadOnlyList<string> Get(string paneId)
    {
        lock (_lock)
            return _byPane.TryGetValue(paneId, out var list) ? list.ToArray() : Array.Empty<string>();
    }

    public IReadOnlyList<string> GetAll()
    {
        lock (_lock)
            return _byPane.Values.SelectMany(v => v).Reverse().Distinct().Take(1000).ToArray();
    }

    public void Remove(string paneId) => _byPane.TryRemove(paneId, out _);
}
