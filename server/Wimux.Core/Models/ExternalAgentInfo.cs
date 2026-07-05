namespace Wimux.Core.Models;

public enum ExternalAgentStatus
{
    Running,
    Waiting,
    Idle,
    Unknown,
}

public enum ExternalAgentType
{
    ClaudeCode,
    GeminiCli,
    Codex,
    OpenCode,
    KiroCli,
    Other,
}

public class ExternalAgentInfo
{
    public string Name { get; set; } = "";
    public ExternalAgentType Type { get; set; }
    public ExternalAgentStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public int Pid { get; set; }
    public string ProjectPath { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTime LastActive { get; set; }
    public string? SessionFilePath { get; set; }

    public string TypeLabel => Type switch
    {
        ExternalAgentType.ClaudeCode => "claude",
        ExternalAgentType.GeminiCli  => "gemini",
        ExternalAgentType.Codex      => "codex",
        ExternalAgentType.OpenCode   => "opencode",
        ExternalAgentType.KiroCli    => "kiro",
        _                            => "other",
    };

    public string StatusLabel => Status switch
    {
        ExternalAgentStatus.Running => "run",
        ExternalAgentStatus.Waiting => "wait",
        ExternalAgentStatus.Idle    => "idle",
        _                           => "?",
    };
}

public class ExternalAgentMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; }
}
