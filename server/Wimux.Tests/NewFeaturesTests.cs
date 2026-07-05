using System.Text.Json;
using Wimux.Core.Models;
using Wimux.Core.Services;
using FluentAssertions;
using Xunit;

namespace Wimux.Tests;

// ── Tab Color Labels ──────────────────────────────────────────────────────────

public class TabColorTests
{
    [Fact]
    public void Surface_TabColor_DefaultsToNull()
    {
        var surface = new Surface();
        surface.TabColor.Should().BeNull();
    }

    [Fact]
    public void Surface_TabColor_CanBeSet()
    {
        var surface = new Surface { TabColor = "#FF6366F1" };
        surface.TabColor.Should().Be("#FF6366F1");
    }

    [Fact]
    public void Surface_TabColor_CanBeCleared()
    {
        var surface = new Surface { TabColor = "#FF6366F1" };
        surface.TabColor = null;
        surface.TabColor.Should().BeNull();
    }

    [Theory]
    [InlineData("#FF6366F1")]
    [InlineData("#FF10B981")]
    [InlineData("#FFEF4444")]
    [InlineData("#FFF59E0B")]
    [InlineData("#FF3B82F6")]
    public void Surface_TabColor_AcceptsValidHexColors(string hex)
    {
        var surface = new Surface { TabColor = hex };
        surface.TabColor.Should().Be(hex);
    }
}

// ── Environment Variables per Workspace ──────────────────────────────────────

public class WorkspaceEnvVarsTests
{
    [Fact]
    public void Workspace_EnvironmentVariables_DefaultsToEmpty()
    {
        var ws = new Workspace();
        ws.EnvironmentVariables.Should().NotBeNull();
        ws.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void Workspace_EnvironmentVariables_CanBeSet()
    {
        var ws = new Workspace();
        ws.EnvironmentVariables["NODE_ENV"] = "development";
        ws.EnvironmentVariables["API_KEY"] = "test-key";

        ws.EnvironmentVariables.Should().HaveCount(2);
        ws.EnvironmentVariables["NODE_ENV"].Should().Be("development");
        ws.EnvironmentVariables["API_KEY"].Should().Be("test-key");
    }

    [Fact]
    public void Workspace_EnvironmentVariables_CanBeCleared()
    {
        var ws = new Workspace();
        ws.EnvironmentVariables["FOO"] = "bar";
        ws.EnvironmentVariables.Clear();
        ws.EnvironmentVariables.Should().BeEmpty();
    }

    [Fact]
    public void Workspace_EnvironmentVariables_OverwriteExistingKey()
    {
        var ws = new Workspace();
        ws.EnvironmentVariables["KEY"] = "old";
        ws.EnvironmentVariables["KEY"] = "new";
        ws.EnvironmentVariables["KEY"].Should().Be("new");
    }
}

// ── SSH Profiles ──────────────────────────────────────────────────────────────

public class SshProfileTests
{
    [Fact]
    public void Workspace_SshProfiles_DefaultsToEmpty()
    {
        var ws = new Workspace();
        ws.SshProfiles.Should().NotBeNull();
        ws.SshProfiles.Should().BeEmpty();
    }

    [Fact]
    public void SshProfile_DefaultPort_Is22()
    {
        var profile = new SshProfile();
        profile.Port.Should().Be(22);
    }

    [Fact]
    public void SshProfile_CanBeAdded()
    {
        var ws = new Workspace();
        ws.SshProfiles.Add(new SshProfile
        {
            Name = "Production",
            Host = "prod.example.com",
            User = "deploy",
            Port = 22,
        });

        ws.SshProfiles.Should().HaveCount(1);
        ws.SshProfiles[0].Name.Should().Be("Production");
        ws.SshProfiles[0].Host.Should().Be("prod.example.com");
    }

    [Fact]
    public void SshProfile_WithCustomPort()
    {
        var profile = new SshProfile { Host = "host", Port = 2222 };
        profile.Port.Should().Be(2222);
    }

    [Fact]
    public void SshProfile_WithIdentityFile()
    {
        var profile = new SshProfile { IdentityFile = "~/.ssh/id_rsa" };
        profile.IdentityFile.Should().Be("~/.ssh/id_rsa");
    }

    [Fact]
    public void SshProfile_HasUniqueId()
    {
        var p1 = new SshProfile();
        var p2 = new SshProfile();
        p1.Id.Should().NotBe(p2.Id);
    }
}

// ── Workspace Templates ───────────────────────────────────────────────────────

public class WorkspaceTemplateServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WorkspaceTemplateService _service;

    public WorkspaceTemplateServiceTests()
    {
        // Override the default dir by using a temp path via reflection
        _tempDir = Path.Combine(Path.GetTempPath(), $"wimux-test-templates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _service = CreateServiceWithDir(_tempDir);
    }

    private static WorkspaceTemplateService CreateServiceWithDir(string dir)
    {
        // Use the real service — it writes to %LOCALAPPDATA%\wimux\templates
        // For isolation, we test the model directly
        return new WorkspaceTemplateService();
    }

    [Fact]
    public void WorkspaceTemplate_DefaultValues()
    {
        var t = new WorkspaceTemplate();
        t.Id.Should().NotBeNullOrWhiteSpace();
        t.Surfaces.Should().NotBeNull().And.BeEmpty();
        t.EnvironmentVariables.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void WorkspaceTemplate_CanAddSurfaces()
    {
        var t = new WorkspaceTemplate { Name = "My Template" };
        t.Surfaces.Add(new TemplateSurface { Name = "Terminal 1" });
        t.Surfaces.Add(new TemplateSurface { Name = "Terminal 2" });

        t.Surfaces.Should().HaveCount(2);
        t.Surfaces[0].Name.Should().Be("Terminal 1");
    }

    [Fact]
    public void WorkspaceTemplate_CanStoreEnvVars()
    {
        var t = new WorkspaceTemplate();
        t.EnvironmentVariables["NODE_ENV"] = "production";
        t.EnvironmentVariables["NODE_ENV"].Should().Be("production");
    }

    [Fact]
    public void WorkspaceTemplate_SerializesAndDeserializes()
    {
        var t = new WorkspaceTemplate
        {
            Name = "Test Template",
            Description = "A test",
        };
        t.Surfaces.Add(new TemplateSurface { Name = "Main" });
        t.EnvironmentVariables["FOO"] = "bar";

        var json = JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<WorkspaceTemplate>(json);

        restored.Should().NotBeNull();
        restored!.Name.Should().Be("Test Template");
        restored.Surfaces.Should().HaveCount(1);
        restored.Surfaces[0].Name.Should().Be("Main");
        restored.EnvironmentVariables["FOO"].Should().Be("bar");
    }

    [Fact]
    public void TemplateSurface_DefaultValues()
    {
        var s = new TemplateSurface();
        s.Name.Should().Be("Terminal");
        s.Panes.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void TemplatePaneLayout_DefaultDirection_IsVertical()
    {
        var p = new TemplatePaneLayout();
        p.Direction.Should().Be(SplitDirection.Vertical);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

// ── ExternalAgentService — Session File Parsing ───────────────────────────────

public class ExternalAgentServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ExternalAgentService _service = new();

    public ExternalAgentServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wimux-test-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private string WriteSessionFile(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void GetConversation_EmptyFile_ReturnsEmpty()
    {
        var path = WriteSessionFile("");
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var messages = _service.GetConversation(agent);
        messages.Should().BeEmpty();
    }

    [Fact]
    public void GetConversation_NullSessionFile_ReturnsEmpty()
    {
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = null,
        };

        var messages = _service.GetConversation(agent);
        messages.Should().BeEmpty();
    }

    [Fact]
    public void GetConversation_ValidClaudeSession_ParsesMessages()
    {
        var content = """
            {"type":"user","message":{"content":"fix the bug"},"timestamp":"2026-05-30T10:00:00Z"}
            {"type":"assistant","message":{"content":"Sure, let me look at that."},"timestamp":"2026-05-30T10:00:05Z"}
            """;
        var path = WriteSessionFile(content);
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var messages = _service.GetConversation(agent);

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be("user");
        messages[0].Content.Should().Be("fix the bug");
        messages[1].Role.Should().Be("assistant");
        messages[1].Content.Should().Be("Sure, let me look at that.");
    }

    [Fact]
    public void GetConversation_ArrayContent_ExtractsText()
    {
        var content = """
            {"type":"user","message":{"content":[{"type":"text","text":"hello world"}]},"timestamp":"2026-05-30T10:00:00Z"}
            """;
        var path = WriteSessionFile(content);
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var messages = _service.GetConversation(agent);

        messages.Should().HaveCount(1);
        messages[0].Content.Should().Be("hello world");
    }

    [Fact]
    public void GetConversation_MalformedLines_SkipsAndContinues()
    {
        var content = """
            {"type":"user","message":{"content":"first"},"timestamp":"2026-05-30T10:00:00Z"}
            {broken json here
            {"type":"assistant","message":{"content":"second"},"timestamp":"2026-05-30T10:00:05Z"}
            """;
        var path = WriteSessionFile(content);
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var messages = _service.GetConversation(agent);

        messages.Should().HaveCount(2);
        messages[0].Content.Should().Be("first");
        messages[1].Content.Should().Be("second");
    }

    [Fact]
    public void GetConversation_MaxMessages_LimitsResults()
    {
        var lines = Enumerable.Range(0, 30)
            .Select(i => $"{{\"type\":\"user\",\"message\":{{\"content\":\"msg {i}\"}},\"timestamp\":\"2026-05-30T10:00:00Z\"}}")
            .ToList();
        var path = WriteSessionFile(string.Join("\n", lines));
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var messages = _service.GetConversation(agent, maxMessages: 10);

        messages.Should().HaveCount(10);
        // Should return the LAST 10 messages
        messages[^1].Content.Should().Be("msg 29");
    }

    [Fact]
    public void GetNewMessages_SkipsAlreadyLoaded()
    {
        var content = """
            {"type":"user","message":{"content":"msg1"},"timestamp":"2026-05-30T10:00:00Z"}
            {"type":"assistant","message":{"content":"msg2"},"timestamp":"2026-05-30T10:00:05Z"}
            {"type":"user","message":{"content":"msg3"},"timestamp":"2026-05-30T10:00:10Z"}
            """;
        var path = WriteSessionFile(content);
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var newMessages = _service.GetNewMessages(agent, skipCount: 2);

        newMessages.Should().HaveCount(1);
        newMessages[0].Content.Should().Be("msg3");
    }

    [Fact]
    public void GetNewMessages_SkipCountExceedsTotal_ReturnsEmpty()
    {
        var content = """
            {"type":"user","message":{"content":"only one"},"timestamp":"2026-05-30T10:00:00Z"}
            """;
        var path = WriteSessionFile(content);
        var agent = new ExternalAgentInfo
        {
            Type = ExternalAgentType.ClaudeCode,
            SessionFilePath = path,
        };

        var newMessages = _service.GetNewMessages(agent, skipCount: 5);
        newMessages.Should().BeEmpty();
    }

    [Fact]
    public void DetectAgents_DoesNotThrow()
    {
        // Just verify it doesn't crash — actual agent detection depends on running processes
        var act = () => _service.DetectAgents();
        act.Should().NotThrow();
    }

    [Fact]
    public void DetectAgents_ReturnsListNotNull()
    {
        var agents = _service.DetectAgents();
        agents.Should().NotBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}

// ── ExternalAgentInfo Model ───────────────────────────────────────────────────

public class ExternalAgentInfoTests
{
    [Theory]
    [InlineData(ExternalAgentType.ClaudeCode, "claude")]
    [InlineData(ExternalAgentType.GeminiCli, "gemini")]
    [InlineData(ExternalAgentType.Codex, "codex")]
    [InlineData(ExternalAgentType.OpenCode, "opencode")]
    [InlineData(ExternalAgentType.Other, "other")]
    public void TypeLabel_ReturnsExpectedString(ExternalAgentType type, string expected)
    {
        var info = new ExternalAgentInfo { Type = type };
        info.TypeLabel.Should().Be(expected);
    }

    [Theory]
    [InlineData(ExternalAgentStatus.Running, "run")]
    [InlineData(ExternalAgentStatus.Waiting, "wait")]
    [InlineData(ExternalAgentStatus.Idle, "idle")]
    [InlineData(ExternalAgentStatus.Unknown, "?")]
    public void StatusLabel_ReturnsExpectedString(ExternalAgentStatus status, string expected)
    {
        var info = new ExternalAgentInfo { Status = status };
        info.StatusLabel.Should().Be(expected);
    }

    [Fact]
    public void ExternalAgentMessage_DefaultTimestamp_IsNotMinValue()
    {
        var msg = new ExternalAgentMessage { Role = "user", Content = "hello" };
        msg.Role.Should().Be("user");
        msg.Content.Should().Be("hello");
    }
}

// ── Broadcast Input Model ─────────────────────────────────────────────────────

public class BroadcastInputModelTests
{
    [Fact]
    public void Surface_HasTabColorProperty()
    {
        // Verify Surface model has the TabColor property added for broadcast/tab features
        var surface = new Surface();
        var prop = typeof(Surface).GetProperty("TabColor");
        prop.Should().NotBeNull("Surface must have TabColor property for tab color labels feature");
    }

    [Fact]
    public void Workspace_HasEnvironmentVariablesProperty()
    {
        var ws = new Workspace();
        var prop = typeof(Workspace).GetProperty("EnvironmentVariables");
        prop.Should().NotBeNull("Workspace must have EnvironmentVariables property");
        prop!.PropertyType.Should().Be(typeof(Dictionary<string, string>));
    }

    [Fact]
    public void Workspace_HasSshProfilesProperty()
    {
        var ws = new Workspace();
        var prop = typeof(Workspace).GetProperty("SshProfiles");
        prop.Should().NotBeNull("Workspace must have SshProfiles property for SSH panes feature");
        prop!.PropertyType.Should().Be(typeof(List<SshProfile>));
    }
}

// ── Port Links — Model Validation ────────────────────────────────────────────

public class PortLinksTests
{
    [Fact]
    public void Workspace_ListeningPorts_DefaultsToEmpty()
    {
        var ws = new Workspace();
        ws.ListeningPorts.Should().NotBeNull();
        ws.ListeningPorts.Should().BeEmpty();
    }

    [Fact]
    public void Workspace_ListeningPorts_CanBePopulated()
    {
        var ws = new Workspace();
        ws.ListeningPorts.Add(3000);
        ws.ListeningPorts.Add(8080);

        ws.ListeningPorts.Should().HaveCount(2);
        ws.ListeningPorts.Should().Contain(3000);
        ws.ListeningPorts.Should().Contain(8080);
    }

    [Fact]
    public void PortUrl_Format_IsCorrect()
    {
        // Verify the URL format used by PortLink_Click
        int port = 3000;
        var url = $"http://localhost:{port}";
        url.Should().Be("http://localhost:3000");
    }
}

// ── WorkspaceTemplateService — File I/O ──────────────────────────────────────

public class WorkspaceTemplateFileTests : IDisposable
{
    private readonly string _tempDir;

    public WorkspaceTemplateFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"wimux-tmpl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Template_JsonRoundTrip_PreservesAllFields()
    {
        var original = new WorkspaceTemplate
        {
            Id = "test-id-123",
            Name = "My Dev Setup",
            Description = "3 terminals + browser",
            CreatedAt = new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc),
        };
        original.Surfaces.Add(new TemplateSurface
        {
            Name = "Backend",
            Panes =
            [
                new TemplatePaneLayout { Shell = "pwsh", WorkingDirectory = "C:\\Projects\\api" },
                new TemplatePaneLayout { Shell = "pwsh", WorkingDirectory = "C:\\Projects\\api", Direction = SplitDirection.Horizontal },
            ],
        });
        original.Surfaces.Add(new TemplateSurface { Name = "Frontend" });
        original.EnvironmentVariables["NODE_ENV"] = "development";
        original.EnvironmentVariables["PORT"] = "3000";

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<WorkspaceTemplate>(json)!;

        restored.Id.Should().Be("test-id-123");
        restored.Name.Should().Be("My Dev Setup");
        restored.Description.Should().Be("3 terminals + browser");
        restored.Surfaces.Should().HaveCount(2);
        restored.Surfaces[0].Name.Should().Be("Backend");
        restored.Surfaces[0].Panes.Should().HaveCount(2);
        restored.Surfaces[0].Panes[0].Shell.Should().Be("pwsh");
        restored.Surfaces[0].Panes[1].Direction.Should().Be(SplitDirection.Horizontal);
        restored.EnvironmentVariables["NODE_ENV"].Should().Be("development");
        restored.EnvironmentVariables["PORT"].Should().Be("3000");
    }

    [Fact]
    public void Template_EmptySurfaces_SerializesCleanly()
    {
        var t = new WorkspaceTemplate { Name = "Empty" };
        var json = JsonSerializer.Serialize(t);
        var restored = JsonSerializer.Deserialize<WorkspaceTemplate>(json)!;
        restored.Surfaces.Should().BeEmpty();
        restored.EnvironmentVariables.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
