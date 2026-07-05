using System.Text.Json;
using Wimux.Core.Models;

namespace Wimux.Core.Services;

/// <summary>
/// Parses Claude Code JSONL session files.
/// Ported from ai-devkit ClaudeSessionParser.ts.
/// </summary>
public class ClaudeSessionParser
{
    // Entry types that carry real conversational state.
    // UI-state events (permission-mode, ai-title, queued_command, tools_changed,
    // model_changed, hook_progress, attachment) are excluded because they trail
    // after the real last conversational entry and would mask actual status.
    private static readonly HashSet<string> ConversationEntryTypes = new(StringComparer.Ordinal)
    {
        "user", "assistant", "system", "progress", "thinking",
    };

    public record ClaudeSession(
        string SessionId,
        string? LastUserMessage,
        string? FirstUserMessage,
        string? LastCwd,
        string? LastEntryType,
        bool IsInterrupted,
        DateTime LastActive,
        DateTime? SessionStart
    );

    public ClaudeSession? ReadSession(string filePath, string defaultCwd)
    {
        try
        {
            string? sessionId = null;
            string? firstUserMessage = null;
            string? lastUserMessage = null;
            string? lastCwd = defaultCwd;
            string? lastEntryType = null;
            bool isInterrupted = false;
            DateTime lastActive = DateTime.MinValue;
            DateTime? sessionStart = null;

            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Extract sessionId from system init entries
                    if (root.TryGetProperty("type", out var typeEl))
                    {
                        var entryType = typeEl.GetString() ?? "";

                        if (entryType == "system" &&
                            root.TryGetProperty("subtype", out var subEl) &&
                            subEl.GetString() == "init")
                        {
                            if (root.TryGetProperty("sessionId", out var sidEl))
                                sessionId = sidEl.GetString();

                            if (root.TryGetProperty("cwd", out var cwdEl))
                                lastCwd = cwdEl.GetString() ?? lastCwd;

                            if (sessionStart == null)
                                sessionStart = TryParseTimestamp(root);
                        }

                        // Check for interruption marker
                        if (entryType == "user" &&
                            root.TryGetProperty("message", out var msgEl) &&
                            msgEl.TryGetProperty("content", out var contentEl))
                        {
                            var text = ExtractTextContent(contentEl);
                            if (text.Contains("[Request interrupted", StringComparison.OrdinalIgnoreCase))
                                isInterrupted = true;
                        }

                        if (!ConversationEntryTypes.Contains(entryType))
                            continue;

                        lastEntryType = entryType;
                        var ts = TryParseTimestamp(root);
                        if (ts > lastActive) lastActive = ts;

                        if (entryType == "user" &&
                            root.TryGetProperty("message", out var umsgEl) &&
                            umsgEl.TryGetProperty("content", out var ucontentEl))
                        {
                            var text = ExtractTextContent(ucontentEl);
                            // Skip injected harness noise
                            if (!string.IsNullOrWhiteSpace(text) &&
                                !text.Contains("[Request interrupted", StringComparison.OrdinalIgnoreCase) &&
                                !text.StartsWith("Tool loaded.", StringComparison.OrdinalIgnoreCase) &&
                                !text.StartsWith("This session is being continued", StringComparison.OrdinalIgnoreCase))
                            {
                                if (firstUserMessage == null)
                                    firstUserMessage = Truncate(text, 120);
                                lastUserMessage = Truncate(text, 120);
                            }
                        }
                    }
                }
                catch { }
            }

            if (lastActive == DateTime.MinValue)
                lastActive = File.GetLastWriteTime(filePath);

            return new ClaudeSession(
                SessionId: sessionId ?? Path.GetFileNameWithoutExtension(filePath),
                LastUserMessage: lastUserMessage,
                FirstUserMessage: firstUserMessage,
                LastCwd: lastCwd,
                LastEntryType: lastEntryType,
                IsInterrupted: isInterrupted,
                LastActive: lastActive,
                SessionStart: sessionStart
            );
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Determines agent status from session data.
    /// Mirrors ai-devkit determineStatus() table:
    ///   user  + not interrupted → RUNNING
    ///   user  + interrupted     → WAITING
    ///   assistant               → WAITING
    ///   progress / thinking     → RUNNING
    ///   system                  → IDLE
    ///   (none)                  → UNKNOWN
    /// </summary>
    public ExternalAgentStatus DetermineStatus(ClaudeSession session)
    {
        return session.LastEntryType switch
        {
            null or "" => ExternalAgentStatus.Unknown,
            "user" => session.IsInterrupted ? ExternalAgentStatus.Waiting : ExternalAgentStatus.Running,
            "assistant" => ExternalAgentStatus.Waiting,
            "progress" or "thinking" => ExternalAgentStatus.Running,
            "system" => ExternalAgentStatus.Idle,
            _ => ExternalAgentStatus.Unknown,
        };
    }

    public List<ExternalAgentMessage> GetConversation(string filePath, int maxMessages = 50)
    {
        var messages = new List<ExternalAgentMessage>();
        try
        {
            foreach (var line in File.ReadLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl)) continue;
                    var msgType = typeEl.GetString() ?? "";
                    if (msgType is not ("user" or "assistant")) continue;
                    if (!root.TryGetProperty("message", out var msgEl)) continue;
                    if (!msgEl.TryGetProperty("content", out var contentEl)) continue;

                    var content = ExtractTextContent(contentEl);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    // Skip harness injections
                    if (content.Contains("[Request interrupted", StringComparison.OrdinalIgnoreCase)) continue;
                    if (content.StartsWith("Tool loaded.", StringComparison.OrdinalIgnoreCase)) continue;
                    if (content.StartsWith("This session is being continued", StringComparison.OrdinalIgnoreCase)) continue;

                    var ts = TryParseTimestamp(root);
                    messages.Add(new ExternalAgentMessage
                    {
                        Role = msgType,
                        Content = content,
                        Timestamp = ts == DateTime.MinValue ? DateTime.Now : ts,
                    });
                }
                catch { }
            }
        }
        catch { }

        return messages.TakeLast(maxMessages).ToList();
    }

    private static DateTime TryParseTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var tsEl))
        {
            var s = tsEl.GetString();
            if (s != null && DateTime.TryParse(s, out var dt))
                return dt;
        }
        return DateTime.MinValue;
    }

    public static string ExtractTextContent(JsonElement contentEl)
    {
        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? "";

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contentEl.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.GetString() == "text" &&
                    item.TryGetProperty("text", out var textEl))
                    return textEl.GetString() ?? "";
            }
        }

        return "";
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
