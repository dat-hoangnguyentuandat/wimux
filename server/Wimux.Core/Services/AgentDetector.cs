namespace Wimux.Core.Services;

public enum AgentType
{
    None, ClaudeCode, Codex, Aider, GithubCopilot, Cursor, Cline, Windsurf
}

public static class AgentDetector
{
    private static readonly (string pattern, AgentType type)[] Patterns =
    [
        ("claude", AgentType.ClaudeCode),
        ("codex", AgentType.Codex),
        ("aider", AgentType.Aider),
        ("copilot", AgentType.GithubCopilot),
        ("cursor", AgentType.Cursor),
        ("cline", AgentType.Cline),
        ("windsurf", AgentType.Windsurf),
    ];

    // Cache per shell PID \u2014 WMI queries are expensive; each PID cached for 10s.
    private static readonly Dictionary<int, (AgentType result, DateTime expiry)> _cache = new();
    private static readonly object _cacheLock = new();

    public static AgentType DetectFromProcessId(int shellPid)
    {
        var now = DateTime.UtcNow;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(shellPid, out var entry) && now < entry.expiry)
                return entry.result;
        }

        AgentType result = AgentType.None;
        try
        {
            var names = GetChildProcessNames(shellPid);
            foreach (var name in names)
                foreach (var (pattern, type) in Patterns)
                    if (name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        result = type;
                        goto done;
                    }
        }
        catch { }

        done:
        lock (_cacheLock)
        {
            _cache[shellPid] = (result, now.AddSeconds(10));
            // Evict stale entries to prevent unbounded growth.
            if (_cache.Count > 64)
            {
                var stale = _cache.Where(kvp => kvp.Value.expiry < now).Select(kvp => kvp.Key).ToList();
                foreach (var k in stale) _cache.Remove(k);
            }
        }
        return result;
    }

    public static string GetLabel(AgentType t) => t switch
    {
        AgentType.ClaudeCode => "Claude Code",
        AgentType.Codex => "Codex",
        AgentType.Aider => "Aider",
        AgentType.GithubCopilot => "Copilot",
        AgentType.Cursor => "Cursor",
        AgentType.Cline => "Cline",
        AgentType.Windsurf => "Windsurf",
        _ => "",
    };

    public static string GetIcon(AgentType t) => t switch
    {
        AgentType.ClaudeCode => "\uE99A",
        AgentType.Codex => "\uE943",
        AgentType.Aider => "\uE8D4",
        AgentType.GithubCopilot => "\uE774",
        AgentType.Cursor => "\uE7C8",
        AgentType.Cline => "\uE8D4",
        AgentType.Windsurf => "\uE774",
        _ => "",
    };

    private static List<string> GetChildProcessNames(int parentPid)
    {
        var names = new List<string>();
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name FROM Win32_Process WHERE ParentProcessId = {parentPid}");
            foreach (var obj in searcher.Get())
                names.Add(obj["Name"]?.ToString() ?? "");
        }
        catch { }
        return names;
    }
}
