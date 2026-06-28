using Cmux.Web.Services.Browser;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

/// <summary>
/// Verifies that two BrowserView panes (each supplying a stable client tabId)
/// get two distinct CDP tabs even when they point at the same URL, and that
/// a reconnect reattaches to the original tab without reopening one.
/// </summary>
public class BrowserManagerTests
{
    private sealed class FakeProvider : IBrowserProvider
    {
        public string Name => "fake";
        public int Opens;
        public Dictionary<string, ProviderTab> Tabs = new();

        public Task EnsureStartedAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<ProviderTab> OpenTabAsync(string url, CancellationToken ct = default)
        {
            Opens++;
            var id = "cdp-" + Opens;
            var t = new ProviderTab(id, "tab " + id, url, null, false)
            {
                DebuggerWebSocketUrl = "ws://fake/" + id,
            };
            Tabs[id] = t;
            return Task.FromResult(t);
        }

        public Task CloseTabAsync(string tabId, CancellationToken ct = default)
        {
            Tabs.Remove(tabId);
            return Task.CompletedTask;
        }
        public Task FocusTabAsync(string tabId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReloadTabAsync(string tabId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ProviderTab>> ListTabsAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<ProviderTab>)Tabs.Values.ToList());

        public Task<ProviderTab?> GetTabAsync(string tabId, CancellationToken ct = default)
        {
            Tabs.TryGetValue(tabId, out var t);
            return Task.FromResult(t);
        }

        public int Quits;
        public Task QuitAsync(CancellationToken ct = default)
        {
            Quits++;
            Tabs.Clear();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task TwoClientTabs_OnSameUrl_OpenTwoDistinctCdpTabs()
    {
        var p = new FakeProvider();
        var m = new BrowserManager(p);

        var a = await m.GetDebuggerTargetAsync("client-A", "https://google.com", forceNew: false);
        var b = await m.GetDebuggerTargetAsync("client-B", "https://google.com", forceNew: false);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        a!.Value.tabId.Should().NotBe(b!.Value.tabId, "each pane must own its own Chrome tab");
        p.Opens.Should().Be(2);
    }

    [Fact]
    public async Task SameClientTab_Reattaches_WithoutOpeningNewTab()
    {
        var p = new FakeProvider();
        var m = new BrowserManager(p);

        var first = await m.GetDebuggerTargetAsync("client-A", "https://google.com", forceNew: false);
        var second = await m.GetDebuggerTargetAsync("client-A", "https://google.com", forceNew: false);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Value.tabId.Should().Be(second!.Value.tabId);
        p.Opens.Should().Be(1, "reconnect with the same client tabId must reuse the existing tab");
    }

    [Fact]
    public async Task CloseClientTab_ClosesUnderlyingCdpTab()
    {
        var p = new FakeProvider();
        var m = new BrowserManager(p);

        var t = await m.GetDebuggerTargetAsync("client-A", "https://example.com", forceNew: false);
        t.Should().NotBeNull();
        p.Tabs.Should().ContainKey(t!.Value.tabId);

        await m.CloseClientTabAsync("client-A");

        p.Tabs.Should().NotContainKey(t.Value.tabId, "closing the cmux3 pane must close the live Chrome tab");

        // Reopening the same client tab allocates a brand-new dedicated tab.
        var t2 = await m.GetDebuggerTargetAsync("client-A", "https://example.com", forceNew: false);
        t2.Should().NotBeNull();
        t2!.Value.tabId.Should().NotBe(t.Value.tabId);
    }

    [Fact]
    public async Task CloseClientTab_UnknownClientTab_IsNoOp()
    {
        var p = new FakeProvider();
        var m = new BrowserManager(p);
        await m.CloseClientTabAsync("never-seen");
        p.Opens.Should().Be(0);
        p.Quits.Should().Be(0);
    }

    [Fact]
    public async Task CloseLastClientTab_QuitsBrowser()
    {
        var p = new FakeProvider();
        var m = new BrowserManager(p);

        await m.GetDebuggerTargetAsync("client-A", "https://example.com", forceNew: false);
        await m.GetDebuggerTargetAsync("client-B", "https://example.com", forceNew: false);

        await m.CloseClientTabAsync("client-A");
        p.Quits.Should().Be(0, "one client tab remains, browser must stay alive");

        await m.CloseClientTabAsync("client-B");
        p.Quits.Should().Be(1, "closing the last client tab must quit the live browser");
    }

    [Fact]
    public async Task CdpTabClosedNatively_ClientTabReopensDedicatedTab()
    {
        var p = new FakeProvider();
        var m = new BrowserManager(p);

        var first = await m.GetDebuggerTargetAsync("client-A", "https://google.com", forceNew: false);
        // simulate the user closing the Chrome tab natively
        await p.CloseTabAsync(first!.Value.tabId);
        // BrowserManager.SyncTabsAsync would normally clean the mapping; emulate that:
        await m.SyncTabsAsync();

        var second = await m.GetDebuggerTargetAsync("client-A", "https://google.com", forceNew: false);
        second.Should().NotBeNull();
        second!.Value.tabId.Should().NotBe(first.Value.tabId, "must allocate a new dedicated CDP tab after native close");
    }
}



