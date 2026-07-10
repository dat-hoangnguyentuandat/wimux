using System.Collections.Concurrent;

namespace Wimux.Web.Services;

public sealed class BrowserSessionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();

    public int ActiveCount => _sessions.Count;

    public void Open(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        _sessions[id] = DateTimeOffset.UtcNow;
    }

    public void Ping(string id)
    {
        Open(id);
    }

    public void Close(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        _sessions.TryRemove(id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            Sweep();
        }
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, lastSeen) in _sessions)
        {
            if (now - lastSeen > SessionTimeout)
                _sessions.TryRemove(id, out _);
        }
    }
}
