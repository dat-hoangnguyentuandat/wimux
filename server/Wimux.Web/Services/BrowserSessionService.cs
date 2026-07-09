using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;

namespace Wimux.Web.Services;

public sealed class BrowserSessionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan EmptyGrace = TimeSpan.FromSeconds(3);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private readonly IHostApplicationLifetime _lifetime;
    private volatile bool _hasSeenBrowser;
    private DateTimeOffset? _emptySince;

    public BrowserSessionService(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public int ActiveCount => _sessions.Count;

    public void Open(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

        _hasSeenBrowser = true;
        _sessions[id] = DateTimeOffset.UtcNow;
        _emptySince = null;
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
        if (_hasSeenBrowser && _sessions.IsEmpty)
            _emptySince ??= DateTimeOffset.UtcNow;
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

        if (!_hasSeenBrowser || !_sessions.IsEmpty)
        {
            _emptySince = null;
            return;
        }

        _emptySince ??= now;
        if (now - _emptySince >= EmptyGrace)
            _lifetime.StopApplication();
    }
}
