namespace Cmux.Web.Services.Browser;

/// <summary>
/// Abstraction over a native browser controlled via a remote-debugging protocol.
/// Implementations: Chrome/Edge (DevTools Protocol), Firefox (Remote Debug), etc.
/// The manager never hardcodes a concrete browser; it talks to this interface only.
/// </summary>
public interface IBrowserProvider : IAsyncDisposable
{
    /// <summary>Human-readable provider name, e.g. "Chrome (CDP)".</summary>
    string Name { get; }

    /// <summary>Ensure the underlying browser is launched and the debug endpoint is reachable.</summary>
    Task EnsureStartedAsync(CancellationToken ct = default);

    /// <summary>Open a new tab navigated to <paramref name="url"/>. Returns the provider tab.</summary>
    Task<ProviderTab> OpenTabAsync(string url, CancellationToken ct = default);

    Task CloseTabAsync(string tabId, CancellationToken ct = default);

    /// <summary>Bring the tab (and the browser window) to the foreground.</summary>
    Task FocusTabAsync(string tabId, CancellationToken ct = default);

    Task ReloadTabAsync(string tabId, CancellationToken ct = default);

    /// <summary>List every live tab the provider knows about.</summary>
    Task<IReadOnlyList<ProviderTab>> ListTabsAsync(CancellationToken ct = default);

    Task<ProviderTab?> GetTabAsync(string tabId, CancellationToken ct = default);

    /// <summary>
    /// Fully shut down the underlying browser process (not just a tab). Used when
    /// the user closes the cmux3 live-browser view so no off-screen browser window
    /// keeps running in the taskbar. Safe to call when nothing is running.
    /// </summary>
    Task QuitAsync(CancellationToken ct = default);
}

/// <summary>Provider-level view of a tab. The manager maps this to BrowserTabState.</summary>
public sealed record ProviderTab(
    string Id,
    string Title,
    string Url,
    string? Favicon,
    bool IsLoading)
{
    /// <summary>CDP per-target debugger WebSocket URL, when known (used for reload/eval).</summary>
    public string? DebuggerWebSocketUrl { get; init; }
}

