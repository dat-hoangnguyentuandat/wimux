using System.Collections.Concurrent;
using System.Diagnostics;

namespace Cmux.Web.Services.Browser;

/// <summary>
/// Owns the native-browser lifecycle for the app. Frontend Browser Cards talk to this
/// service (over WebSocket / REST) instead of controlling Chrome directly.
///
/// Responsibilities: open/close/focus/reload tabs, deduplicate tabs by URL, keep a
/// synced list, and emit events so the frontend never has to poll.
/// The concrete browser is supplied by an <see cref="IBrowserProvider"/> and can be
/// swapped (Chrome CDP, Edge CDP, Firefox RDP) without touching this class.
/// </summary>
public sealed class BrowserManager : IAsyncDisposable
{
    private readonly IBrowserProvider _provider;
    private readonly ConcurrentDictionary<string, BrowserTabState> _tabs = new();
    private readonly ConcurrentDictionary<int, Func<string, object, Task>> _listeners = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _listenerSeq;
    private string? _activeTabId;
    private volatile bool _started;

    // Stable mapping from a client-supplied tab identifier (used by each
    // BrowserView pane) to the dedicated CDP target id it owns. This is what
    // keeps two panes on the same URL (e.g. two google.com tabs in a split)
    // from collapsing onto a single shared Chrome tab. The mapping survives
    // reconnects, so reloading the panel reattaches to the same tab.
    private readonly ConcurrentDictionary<string, string> _clientTabToCdp = new();
    private readonly ConcurrentDictionary<string, string> _cdpToClientTab = new();

    public BrowserManager(IBrowserProvider provider) => _provider = provider;

    public string ProviderName => _provider.Name;

    // ── Subscriptions ───────────────────────────────────────────────────

    /// <summary>Register a client callback. Returns a token to pass to <see cref="Unsubscribe"/>.</summary>
    public int Subscribe(Func<string, object, Task> onEvent)
    {
        var id = Interlocked.Increment(ref _listenerSeq);
        _listeners[id] = onEvent;
        return id;
    }

    public void Unsubscribe(int id) => _listeners.TryRemove(id, out _);

    private async Task Emit(string evt, object payload)
    {
        foreach (var listener in _listeners.Values)
        {
            try { await listener(evt, payload); }
            catch { /* a dead client must not break the others */ }
        }
    }

    // ── Commands ────────────────────────────────────────────────────────

    public async Task<BrowserTabState> OpenTabAsync(string url, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        // Deduplicate: focus an existing tab pointing at the same URL.
        var existing = _tabs.Values.FirstOrDefault(t => UrlsMatch(t.Url, url));
        if (existing != null)
        {
            await FocusTabAsync(existing.Id, ct);
            return _tabs[existing.Id];
        }

        var providerTab = await _provider.OpenTabAsync(url, ct);
        var state = ToState(providerTab, active: true);
        _tabs[state.Id] = state;
        SetActive(state.Id);
        await Emit(BrowserEvent.TabOpened, state);
        await SyncTabsAsync(ct);
        return state;
    }

    public async Task CloseTabAsync(string tabId, CancellationToken ct = default)
    {
        await _provider.CloseTabAsync(tabId, ct);
        _tabs.TryRemove(tabId, out _);
        if (_cdpToClientTab.TryRemove(tabId, out var clientId))
            _clientTabToCdp.TryRemove(clientId, out _);
        if (_activeTabId == tabId) _activeTabId = _tabs.Keys.FirstOrDefault();
        await Emit(BrowserEvent.TabClosed, new { id = tabId });
        await SyncTabsAsync(ct);
    }

    /// <summary>
    /// Close the live Chrome tab bound to a client (pane) tab id. Used when a
    /// cmux3 web pane / tab is closed so the real browser tab is torn down too,
    /// instead of lingering off-screen. No-op if the client tab owns nothing.
    ///
    /// When the last client-owned tab is closed, the underlying browser is shut
    /// down entirely (no off-screen window left in the taskbar).
    /// </summary>
    public async Task CloseClientTabAsync(string clientTabId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clientTabId)) return;
        if (!_clientTabToCdp.TryGetValue(clientTabId, out var cdpId)) return;
        await CloseTabAsync(cdpId, ct);

        if (_clientTabToCdp.IsEmpty)
        {
            // Drop the auto-spawned about:blank tab Chrome opens at launch so the
            // process actually has no children left to keep it alive.
            foreach (var leftover in (await _provider.ListTabsAsync(ct)).ToList())
                try { await _provider.CloseTabAsync(leftover.Id, ct); } catch { /* ignore */ }
            await _provider.QuitAsync(ct);
        }
    }

    public async Task FocusTabAsync(string tabId, CancellationToken ct = default)
    {
        await _provider.FocusTabAsync(tabId, ct);
        SetActive(tabId);
        await Emit(BrowserEvent.TabFocused, new { id = tabId });
        await Emit(BrowserEvent.ActiveTab, GetActiveTab() ?? (object)new { id = (string?)null });
    }

    public async Task ReloadTabAsync(string tabId, CancellationToken ct = default)
    {
        await _provider.ReloadTabAsync(tabId, ct);
        await Emit(BrowserEvent.TabReloaded, new { id = tabId });
        await SyncTabsAsync(ct);
    }

    // ── Queries ─────────────────────────────────────────────────────────

    public IReadOnlyList<BrowserTabState> GetTabs() => _tabs.Values.ToList();

    /// <summary>Ensure the browser is launched (idempotent). Exposed for the screencast bridge.</summary>
    public Task EnsureBrowserAsync(CancellationToken ct = default) => EnsureStartedAsync(ct);

    /// <summary>
    /// Resolve the CDP debugger WebSocket URL for a tab, opening a tab first if none exists.
    /// Used by the screencast view to attach and stream frames.
    /// </summary>
    public async Task<(string tabId, string debuggerWsUrl)?> GetDebuggerTargetAsync(string? tabId, string? fallbackUrl, bool forceNew = false, CancellationToken ct = default)
    {
        await EnsureStartedAsync(ct);

        // Each BrowserView pane supplies a stable client tabId. We map it to a
        // dedicated CDP target so every pane owns its own Chrome tab, even when
        // two panes point at the same URL. The mapping is reused across
        // reconnects (page refresh / Reconnect button) and only a brand-new
        // client tabId — or an explicit forceNew — ever opens a new Chrome tab.
        if (!string.IsNullOrEmpty(tabId))
        {
            // Reattach to the CDP tab this client tab already owns, if it is still alive.
            if (!forceNew && _clientTabToCdp.TryGetValue(tabId, out var mappedCdpId))
            {
                var mapped = await _provider.GetTabAsync(mappedCdpId, ct);
                if (mapped?.DebuggerWebSocketUrl is { Length: > 0 } mws)
                {
                    TrackTab(mapped);
                    return (mapped.Id, mws);
                }
                // The owned tab was closed natively; drop the stale mapping and reopen below.
                ForgetClientTab(tabId);
            }

            // First time we see this client tab (or its tab was closed): open a
            // fresh, dedicated CDP tab and bind it to this client tabId.
            var openUrl = string.IsNullOrEmpty(fallbackUrl) ? "about:blank" : fallbackUrl;
            var fresh = await _provider.OpenTabAsync(openUrl, ct);
            BindClientTab(tabId, fresh.Id);
            TrackTab(fresh);
            return fresh.DebuggerWebSocketUrl is { Length: > 0 } fws ? (fresh.Id, fws) : null;
        }

        // No client tabId supplied (legacy callers). Preserve the old behavior:
        // create a dedicated tab on forceNew, otherwise reuse by URL.
        if (forceNew)
        {
            var fresh = await _provider.OpenTabAsync(string.IsNullOrEmpty(fallbackUrl) ? "about:blank" : fallbackUrl, ct);
            TrackTab(fresh);
            return fresh.DebuggerWebSocketUrl is { Length: > 0 } fws ? (fresh.Id, fws) : null;
        }

        ProviderTab? tab = null;
        if (!string.IsNullOrEmpty(fallbackUrl))
        {
            var existing = _tabs.Values.FirstOrDefault(t => UrlsMatch(t.Url, fallbackUrl));
            if (existing != null)
                tab = await _provider.GetTabAsync(existing.Id, ct);
            tab ??= await _provider.OpenTabAsync(fallbackUrl, ct);
            if (tab != null) TrackTab(tab);
        }

        if (tab?.DebuggerWebSocketUrl is { Length: > 0 } ws)
            return (tab.Id, ws);
        return null;
    }

    private void TrackTab(ProviderTab tab)
    {
        var st = ToState(tab, active: true);
        _tabs[st.Id] = st;
        SetActive(st.Id);
        _ = Emit(BrowserEvent.TabOpened, st);
    }

    private void BindClientTab(string clientTabId, string cdpTabId)
    {
        _clientTabToCdp[clientTabId] = cdpTabId;
        _cdpToClientTab[cdpTabId] = clientTabId;
    }

    private void ForgetClientTab(string clientTabId)
    {
        if (_clientTabToCdp.TryRemove(clientTabId, out var cdpId))
            _cdpToClientTab.TryRemove(cdpId, out _);
    }

    public BrowserTabState? GetActiveTab()
        => _activeTabId != null && _tabs.TryGetValue(_activeTabId, out var t) ? t : null;

    /// <summary>Pull the live tab list from the provider and reconcile local state.</summary>
    public async Task<IReadOnlyList<BrowserTabState>> SyncTabsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            IReadOnlyList<ProviderTab> live;
            try { live = await _provider.ListTabsAsync(ct); }
            catch (BrowserUnavailableException) { live = Array.Empty<ProviderTab>(); }

            var liveIds = new HashSet<string>();
            foreach (var pt in live)
            {
                liveIds.Add(pt.Id);
                var active = pt.Id == _activeTabId;
                var next = ToState(pt, active);
                if (!_tabs.TryGetValue(pt.Id, out var prev) || !Equal(prev, next))
                {
                    _tabs[pt.Id] = next;
                    await Emit(BrowserEvent.TabUpdated, next);
                }
            }

            // Drop tabs the user closed natively. Also drop any client-tab binding
            // that pointed at the now-gone CDP target so the next reconnect for
            // the same pane gets a fresh, dedicated tab.
            foreach (var goneId in _tabs.Keys.Where(id => !liveIds.Contains(id)).ToList())
            {
                _tabs.TryRemove(goneId, out _);
                if (_cdpToClientTab.TryRemove(goneId, out var clientId))
                    _clientTabToCdp.TryRemove(clientId, out _);
                await Emit(BrowserEvent.TabClosed, new { id = goneId });
            }

            var all = _tabs.Values.ToList();
            await Emit(BrowserEvent.AllTabs, all);
            return all;
        }
        finally { _gate.Release(); }
    }

    // Always delegate to the provider, which cheaply checks the debug port and
    // relaunches the browser if it was closed. This lets a "reload" recover a
    // killed browser without restarting cmux3.
    private async Task EnsureStartedAsync(CancellationToken ct)
    {
        await _provider.EnsureStartedAsync(ct);
        _started = true;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private void SetActive(string tabId)
    {
        _activeTabId = tabId;
        foreach (var (id, t) in _tabs)
            _tabs[id] = t with { IsActive = id == tabId };
    }

    private BrowserTabState ToState(ProviderTab t, bool active) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Url = t.Url,
        Favicon = t.Favicon,
        IsLoading = t.IsLoading,
        IsActive = active,
    };

    private static bool Equal(BrowserTabState a, BrowserTabState b)
        => a.Title == b.Title && a.Url == b.Url && a.Favicon == b.Favicon
           && a.IsLoading == b.IsLoading && a.IsActive == b.IsActive;

    private static bool UrlsMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(a, UriKind.Absolute, out var ua)
            && Uri.TryCreate(b, UriKind.Absolute, out var ub)
            && string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ua.AbsolutePath, ub.AbsolutePath, StringComparison.OrdinalIgnoreCase)
            && ua.Query == ub.Query;
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _provider.DisposeAsync();
    }
}







