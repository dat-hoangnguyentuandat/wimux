using Wimux.Core.Models;

namespace Wimux.Core.Services;

public sealed class AgentQuotaService
{
    private readonly AgentConversationStoreService _store;

    public AgentQuotaService(AgentConversationStoreService store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public QuotaSnapshot GetSnapshot()
    {
        var nowUtc = DateTime.UtcNow;
        var allRows = new List<QuotaRow>();
        var perWindow = new Dictionary<QuotaWindow, Dictionary<string, QuotaRowAccumulator>>();

        foreach (var window in (QuotaWindow[])Enum.GetValues(typeof(QuotaWindow)))
            perWindow[window] = new Dictionary<string, QuotaRowAccumulator>(StringComparer.Ordinal);

        var threads = _store.GetAllThreads();
        foreach (var thread in threads)
        {
            var messages = _store.GetMessages(thread.Id, 5000);
            foreach (var msg in messages)
            {
                if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase) && msg.InputTokens == 0 && msg.OutputTokens == 0)
                    continue;

                var provider = string.IsNullOrWhiteSpace(msg.Provider) ? "(unknown)" : msg.Provider.Trim();
                var model = string.IsNullOrWhiteSpace(msg.Model) ? "(unknown)" : msg.Model.Trim();
                var key = $"{provider}|{model}";

                foreach (var window in perWindow.Keys)
                {
                    if (!IsWithinWindow(msg.CreatedAtUtc, nowUtc, window))
                        continue;

                    if (!perWindow[window].TryGetValue(key, out var acc))
                    {
                        acc = new QuotaRowAccumulator { Provider = provider, Model = model };
                        perWindow[window][key] = acc;
                    }

                    if (string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        acc.Requests++;
                    acc.InputTokens += Math.Max(0, msg.InputTokens);
                    acc.OutputTokens += Math.Max(0, msg.OutputTokens);
                    acc.TotalTokens += Math.Max(0, msg.TotalTokens);
                    if (msg.CreatedAtUtc > acc.LastActivityUtc)
                        acc.LastActivityUtc = msg.CreatedAtUtc;
                }
            }
        }

        var snapshot = new QuotaSnapshot
        {
            GeneratedAtUtc = nowUtc,
        };

        foreach (var window in perWindow.Keys)
        {
            var rows = perWindow[window].Values
                .Select(a => new QuotaRow
                {
                    Window = window,
                    Provider = a.Provider,
                    Model = a.Model,
                    Requests = a.Requests,
                    InputTokens = a.InputTokens,
                    OutputTokens = a.OutputTokens,
                    TotalTokens = a.TotalTokens > 0 ? a.TotalTokens : a.InputTokens + a.OutputTokens,
                    LastActivityUtc = a.LastActivityUtc,
                })
                .OrderByDescending(r => r.TotalTokens)
                .ThenBy(r => r.Provider, StringComparer.OrdinalIgnoreCase)
                .ToList();

            snapshot.RowsByWindow[window] = rows;
        }

        return snapshot;
    }

    private static bool IsWithinWindow(DateTime messageUtc, DateTime nowUtc, QuotaWindow window)
    {
        return window switch
        {
            QuotaWindow.Last5Hours => (nowUtc - messageUtc) <= TimeSpan.FromHours(5),
            QuotaWindow.Today => messageUtc.ToLocalTime().Date == nowUtc.ToLocalTime().Date,
            QuotaWindow.Last7Days => (nowUtc - messageUtc) <= TimeSpan.FromDays(7),
            QuotaWindow.Last30Days => (nowUtc - messageUtc) <= TimeSpan.FromDays(30),
            QuotaWindow.AllTime => true,
            _ => true,
        };
    }

    private sealed class QuotaRowAccumulator
    {
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public int Requests { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
        public DateTime LastActivityUtc { get; set; }
    }
}

public enum QuotaWindow
{
    Last5Hours,
    Today,
    Last7Days,
    Last30Days,
    AllTime,
}

public sealed class QuotaRow
{
    public QuotaWindow Window { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public int Requests { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public DateTime LastActivityUtc { get; set; }
    public string LastActivityLocal => LastActivityUtc == default ? "-" : LastActivityUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class QuotaSnapshot
{
    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<QuotaWindow, List<QuotaRow>> RowsByWindow { get; } = new();

    public List<QuotaRow> Get(QuotaWindow window)
    {
        return RowsByWindow.TryGetValue(window, out var rows) ? rows : new List<QuotaRow>();
    }

    public int TotalTokensFor(QuotaWindow window)
    {
        return Get(window).Sum(r => r.TotalTokens);
    }

    public int RequestsFor(QuotaWindow window)
    {
        return Get(window).Sum(r => r.Requests);
    }
}
