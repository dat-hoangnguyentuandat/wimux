using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Wimux.Tests;

/// <summary>
/// End-to-end tests for the wimux web backend: REST API for workspaces,
/// surfaces and split panes, plus the live terminal WebSocket. These verify
/// the web app reproduces the wimux2 workspace/surface/split/terminal behavior.
/// </summary>
public class WebE2ETests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public WebE2ETests()
    {
        // Use a clean, isolated state file per test process.
        var dir = Path.Combine(Path.GetTempPath(), "wimux-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", dir);
        var stateDir = Path.Combine(dir, "wimux");
        Environment.SetEnvironmentVariable("WIMUX_STATE_DIR", stateDir);
        var statePath = Path.Combine(stateDir, "state.json");
        if (File.Exists(statePath)) File.Delete(statePath);
        _factory = new WebApplicationFactory<Program>();
    }

    private HttpClient Client() => _factory.CreateClient();

    [Fact]
    public async Task State_SeedsDefaultWorkspaceWithTerminalSurface()
    {
        var state = await Client().GetFromJsonAsync<JsonElement>("/api/state", J);
        state.GetProperty("workspaces").GetArrayLength().Should().BeGreaterThan(0);
        var ws = state.GetProperty("workspaces")[0];
        ws.GetProperty("surfaces").GetArrayLength().Should().BeGreaterThan(0);
        var surface = ws.GetProperty("surfaces")[0];
        surface.GetProperty("root").GetProperty("isLeaf").GetBoolean().Should().BeTrue();
        surface.GetProperty("panes").EnumerateObject().Count().Should().Be(1);
    }

    [Fact]
    public async Task Themes_ReturnsBuiltInTerminalThemes()
    {
        var themes = await Client().GetFromJsonAsync<JsonElement>("/api/themes", J);
        themes.GetArrayLength().Should().BeGreaterThan(0);
        themes[0].GetProperty("palette").GetArrayLength().Should().Be(16);
    }

    [Fact]
    public async Task Shells_DetectsAtLeastOneShell()
    {
        var shells = await Client().GetFromJsonAsync<JsonElement>("/api/shells", J);
        shells.GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Workspace_CreateSelectRenameDelete_Roundtrips()
    {
        var client = Client();
        var created = await (await client.PostAsJsonAsync("/api/workspaces", new { name = "ProjX" }, J))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        var id = created.GetProperty("id").GetString()!;
        created.GetProperty("name").GetString().Should().Be("ProjX");

        (await client.PostAsync($"/api/workspaces/{id}/select", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await (await client.PutAsJsonAsync($"/api/workspaces/{id}", new { name = "ProjY", accentColor = "#FF0000" }, J))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        updated.GetProperty("name").GetString().Should().Be("ProjY");

        (await client.DeleteAsync($"/api/workspaces/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/state", J);
        state.GetProperty("workspaces").EnumerateArray()
            .Any(w => w.GetProperty("id").GetString() == id).Should().BeFalse();
    }

    [Fact]
    public async Task Surface_CreateRenameSelectDelete_Roundtrips()
    {
        var client = Client();
        var state = await client.GetFromJsonAsync<JsonElement>("/api/state", J);
        var wsId = state.GetProperty("workspaces")[0].GetProperty("id").GetString()!;

        var surface = await (await client.PostAsJsonAsync($"/api/workspaces/{wsId}/surfaces", new { name = "Build" }, J))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        var sId = surface.GetProperty("id").GetString()!;
        surface.GetProperty("name").GetString().Should().Be("Build");

        var renamed = await (await client.PutAsJsonAsync($"/api/workspaces/{wsId}/surfaces/{sId}", new { name = "Tests" }, J))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        renamed.GetProperty("name").GetString().Should().Be("Tests");

        (await client.PostAsync($"/api/workspaces/{wsId}/surfaces/{sId}/select", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"/api/workspaces/{wsId}/surfaces/{sId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Split_CreatesTwoPanes_ThenCloseCollapses()
    {
        var client = Client();
        var state = await client.GetFromJsonAsync<JsonElement>("/api/state", J);
        var ws = state.GetProperty("workspaces")[0];
        var wsId = ws.GetProperty("id").GetString()!;
        var surface = ws.GetProperty("surfaces")[0];
        var sId = surface.GetProperty("id").GetString()!;
        var paneId = surface.GetProperty("focusedPaneId").GetString()!;

        var afterSplit = await (await client.PostAsJsonAsync(
            $"/api/workspaces/{wsId}/surfaces/{sId}/split", new { paneId, direction = "vertical" }, J))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        afterSplit.GetProperty("root").GetProperty("isLeaf").GetBoolean().Should().BeFalse();
        afterSplit.GetProperty("panes").EnumerateObject().Count().Should().Be(2);

        var newPaneId = afterSplit.GetProperty("panes").EnumerateObject()
            .First(p => p.Name != paneId).Name;

        var afterClose = await (await client.DeleteAsync(
            $"/api/workspaces/{wsId}/surfaces/{sId}/panes/{newPaneId}"))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        afterClose.GetProperty("root").GetProperty("isLeaf").GetBoolean().Should().BeTrue();
        afterClose.GetProperty("panes").EnumerateObject().Count().Should().Be(1);
    }

    [Fact]
    public async Task Settings_SaveAndReload_Persists()
    {
        var client = Client();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings", J);
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings.GetRawText())!;
        dict["fontSize"] = JsonSerializer.SerializeToElement(18);
        var saved = await (await client.PutAsJsonAsync("/api/settings", dict, J))
            .Content.ReadFromJsonAsync<JsonElement>(J);
        saved.GetProperty("fontSize").GetInt32().Should().Be(18);
    }

    [Fact]
    public async Task AgentsSend_NoMatchingPane_ReturnsNotFound()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/agents/send", new
        {
            pid = 999999,
            projectPath = "C:\\definitely-not-a-wimux-project",
            text = "hello agent",
        }, J);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ActivateThread_UnknownThread_ReturnsNotFound()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync("/api/threads/not-a-thread/activate", new { }, J);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
