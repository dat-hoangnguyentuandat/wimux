namespace Wimux.Core.Services;

/// <summary>
/// Determines whether a pane should be active (rendering/running) or suspended.
/// Mirrors the browser tab model: hidden panes suspend to save resources, but
/// panes playing audio are never suspended so background media continues uninterrupted.
/// </summary>
public static class PaneVisibilityPolicy
{
    /// <summary>
    /// Returns true if the pane should be running (not suspended).
    /// A pane runs when it is visible OR when it is playing audio.
    /// </summary>
    public static bool ShouldRun(bool isPaneVisible, bool isPlayingAudio)
        => isPaneVisible || isPlayingAudio;

    /// <summary>
    /// Returns true if the pane should be suspended (hidden and not playing audio).
    /// </summary>
    public static bool ShouldSuspend(bool isPaneVisible, bool isPlayingAudio)
        => !ShouldRun(isPaneVisible, isPlayingAudio);

    /// <summary>
    /// Given a set of panes, returns which ones should be running.
    /// Only the active pane is visible; audio panes always run regardless of visibility.
    /// </summary>
    public static IEnumerable<T> GetRunningPanes<T>(
        IEnumerable<T> panes,
        Func<T, bool> isVisible,
        Func<T, bool> isPlayingAudio)
        => panes.Where(p => ShouldRun(isVisible(p), isPlayingAudio(p)));
}
