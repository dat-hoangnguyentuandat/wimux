namespace Cmux.Web.Services.Browser;

/// <summary>Read-only snapshot of a single browser tab, emitted to frontend over WebSocket.</summary>
public sealed record BrowserTabState
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Favicon { get; init; }
    public bool IsLoading { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Event names broadcast from BrowserManager to all WebSocket clients.</summary>
public static class BrowserEvent
{
    public const string TabOpened   = "browser:tab-opened";
    public const string TabClosed   = "browser:tab-closed";
    public const string TabFocused  = "browser:tab-focused";
    public const string TabReloaded = "browser:tab-reloaded";
    public const string TabUpdated  = "browser:tab-updated";
    public const string ActiveTab   = "browser:active-tab";
    public const string AllTabs     = "browser:all-tabs";
    public const string Error       = "browser:error";
}
