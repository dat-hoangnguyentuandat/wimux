using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wimux.Core.Models;

namespace Wimux.Core.Services;

/// <summary>
/// Detects running AI agent CLIs (Claude Code, Codex, Gemini, OpenCode, Kiro)
/// and joins each running process to its session file via the strategies
/// pioneered by ai-devkit (codeaholicguy/ai-devkit):
///   1. PID file at ~/.claude/sessions/&lt;pid&gt;.json (authoritative live status)
///   2. --resume &lt;uuid&gt; in argv
///   3. CWD-encoded project dir + most recent JSONL
/// </summary>
public class ExternalAgentService
{
    private static readonly string HomeDir =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string ClaudeProjectsDir = Path.Combine(HomeDir, ".claude", "projects");
    private static readonly string ClaudeSessionsDir = Path.Combine(HomeDir, ".claude", "sessions");
    private static readonly string CodexSessionsDir  = Path.Combine(HomeDir, ".codex", "sessions");
    private static readonly string GeminiTmpDir      = Path.Combine(HomeDir, ".gemini", "tmp");

    /// PID-file startedAt vs process StartTime tolerance — beyond this we
    /// assume the PID was recycled and the file is stale.
    private const int PidFileStalenessMs = 60_000;

    /// Codex/OpenCode mark IDLE if lastActive older than this (matches ai-devkit).
    private const int IdleThresholdMinutes = 5;

    private static readonly HashSet<string> CandidateProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude", "claude-code", "claude_code",
        "gemini", "gemini-cli",
        "codex", "codex-cli",
        "opencode",
        "kiro", "kiro-cli",
        "node", "node.exe",
    };

    private readonly ClaudeSessionParser _claudeParser = new();

    public List<ExternalAgentInfo> DetectAgents()
    {
        // Step 1: cheap pre-filter by process name
        var candidates = new List<Process>();
        try
        {
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (CandidateProcessNames.Contains(proc.ProcessName))
                        candidates.Add(proc);
                    else
                        proc.Dispose();
                }
                catch
                {
                    try { proc.Dispose(); } catch { }
                }
            }
        }
        catch { }

        if (candidates.Count == 0)
            return [];

        // Step 2: ONE WMI batch query for all candidate PIDs
        var procInfo = BatchQueryProcessInfo(candidates.Select(p => p.Id));

        // Step 3: classify each process and build AgentInfo
        var agents = new List<ExternalAgentInfo>();
        foreach (var proc in candidates)
        {
            try
            {
                procInfo.TryGetValue(proc.Id, out var pi);
                var info = TryClassifyProcess(proc, pi.CommandLine ?? "", pi.WorkingDirectory ?? "");
                if (info != null)
                    agents.Add(info);
            }
            catch { }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }

        return agents
            .GroupBy(GetAgentIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(a => !string.IsNullOrWhiteSpace(a.SessionFilePath))
                .ThenByDescending(a => a.LastActive)
                .ThenBy(a => a.Pid)
                .First())
            .OrderBy(a => a.Status)
            .ThenBy(a => a.Name)
            .ToList();
    }

    private readonly struct ProcInfo
    {
        public string? CommandLine { get; init; }
        public string? WorkingDirectory { get; init; }
    }

    private static Dictionary<int, ProcInfo> BatchQueryProcessInfo(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, ProcInfo>();
        var pidList = pids.ToList();
        if (pidList.Count == 0)
            return result;

        const int chunkSize = 50;
        for (var offset = 0; offset < pidList.Count; offset += chunkSize)
        {
            var chunk = pidList.Skip(offset).Take(chunkSize).ToList();
            var filter = new StringBuilder();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) filter.Append(" OR ");
                filter.Append("ProcessId=").Append(chunk[i]);
            }

            var batchOk = false;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId, CommandLine, WorkingDirectory FROM Win32_Process WHERE {filter}");
                foreach (ManagementObject obj in searcher.Get())
                {
                    try
                    {
                        if (TryGetPid(obj["ProcessId"], out var pid))
                        {
                            result[pid] = new ProcInfo
                            {
                                CommandLine = obj["CommandLine"]?.ToString(),
                                WorkingDirectory = obj["WorkingDirectory"]?.ToString(),
                            };
                            batchOk = true;
                        }
                    }
                    finally
                    {
                        obj.Dispose();
                    }
                }
            }
            catch { }

            // Fallback to per-PID queries if batch failed
            if (!batchOk)
            {
                foreach (var pid in chunk)
                {
                    if (result.ContainsKey(pid)) continue;
                    try
                    {
                        using var searcher = new ManagementObjectSearcher(
                            $"SELECT ProcessId, CommandLine, WorkingDirectory FROM Win32_Process WHERE ProcessId={pid}");
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            try
                            {
                                result[pid] = new ProcInfo
                                {
                                    CommandLine = obj["CommandLine"]?.ToString(),
                                    WorkingDirectory = obj["WorkingDirectory"]?.ToString(),
                                };
                            }
                            finally
                            {
                                obj.Dispose();
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        return result;
    }

    private static bool TryGetPid(object? value, out int pid)
    {
        switch (value)
        {
            case uint u: pid = (int)u; return true;
            case int i: pid = i; return true;
            case long l: pid = (int)l; return true;
            case ulong ul: pid = (int)ul; return true;
            case null: pid = 0; return false;
            default: return int.TryParse(value.ToString(), out pid);
        }
    }

    private ExternalAgentInfo? TryClassifyProcess(Process proc, string cmdLine, string cwd)
    {
        string name;
        DateTime? startTime = null;
        try
        {
            name = proc.ProcessName.ToLowerInvariant();
            try { startTime = proc.StartTime; } catch { }
        }
        catch { return null; }

        ExternalAgentType? type = null;

        if (name is "claude" or "claude-code" or "claude_code")
        {
            type = ExternalAgentType.ClaudeCode;
        }
        else if (name is "gemini" or "gemini-cli")
        {
            type = ExternalAgentType.GeminiCli;
        }
        else if (name is "codex" or "codex-cli")
        {
            type = ExternalAgentType.Codex;
        }
        else if (name is "opencode")
        {
            type = ExternalAgentType.OpenCode;
        }
        else if (name is "kiro" or "kiro-cli")
        {
            type = ExternalAgentType.KiroCli;
        }
        else if (name is "node" or "node.exe")
        {
            // Disambiguate node by command line
            if (IsNodeCliInvocation(cmdLine, "claude"))
                type = ExternalAgentType.ClaudeCode;
            else if (IsNodeCliInvocation(cmdLine, "gemini"))
                type = ExternalAgentType.GeminiCli;
            else if (IsNodeCliInvocation(cmdLine, "codex"))
                type = ExternalAgentType.Codex;
            else if (IsNodeCliInvocation(cmdLine, "opencode"))
                type = ExternalAgentType.OpenCode;
            else if (IsNodeCliInvocation(cmdLine, "kiro"))
                type = ExternalAgentType.KiroCli;
        }

        if (type == null)
            return null;

        return type switch
        {
            ExternalAgentType.ClaudeCode => DetectClaudeCode(proc.Id, cmdLine, cwd, startTime),
            ExternalAgentType.GeminiCli  => DetectGeminiCli(proc.Id, cwd),
            ExternalAgentType.Codex      => DetectCodex(proc.Id, cwd, startTime),
            _                            => BuildProcessOnlyAgent(type.Value, proc.Id, cwd),
        };
    }

    // ── Claude Code ──────────────────────────────────────────────────────────

    private ExternalAgentInfo DetectClaudeCode(int pid, string cmdLine, string cwd, DateTime? startTime)
    {
        // Step 1: try authoritative PID file at ~/.claude/sessions/<pid>.json
        var pidEntry = ReadMatchingPidFile(pid, startTime);

        string? sessionId = pidEntry?.SessionId;
        string? sessionCwd = pidEntry?.Cwd ?? cwd;

        // Step 2: extract --resume <uuid> from command line
        if (string.IsNullOrWhiteSpace(sessionId))
            sessionId = ExtractResumeSessionId(cmdLine);

        string? sessionFile = null;
        if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(sessionCwd))
        {
            var projectDir = GetClaudeProjectDir(sessionCwd);
            var jsonlPath = Path.Combine(projectDir, $"{sessionId}.jsonl");
            if (File.Exists(jsonlPath))
                sessionFile = jsonlPath;
        }

        // Step 3: fallback to most recent JSONL in encoded project dir
        if (sessionFile == null && !string.IsNullOrWhiteSpace(cwd))
        {
            var projectDir = GetClaudeProjectDir(cwd);
            if (Directory.Exists(projectDir))
            {
                try
                {
                    sessionFile = Directory.EnumerateFiles(projectDir, "*.jsonl", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTime)
                        .FirstOrDefault();
                }
                catch { }
            }
        }

        ClaudeSessionParser.ClaudeSession? session = null;
        if (sessionFile != null)
            session = _claudeParser.ReadSession(sessionFile, sessionCwd ?? cwd);

        // Status: PID file wins over JSONL heuristic
        ExternalAgentStatus status;
        if (pidEntry?.MappedStatus is { } pidStatus)
            status = pidStatus;
        else if (session != null)
            status = _claudeParser.DetermineStatus(session);
        else
            status = ExternalAgentStatus.Idle;

        var summary = session?.LastUserMessage ?? "Claude Code";
        if (status == ExternalAgentStatus.Waiting && !string.IsNullOrWhiteSpace(pidEntry?.WaitingFor))
            summary = $"{summary} — waiting for {pidEntry.WaitingFor}";

        var resolvedCwd = session?.LastCwd ?? sessionCwd ?? cwd;

        return new ExternalAgentInfo
        {
            Name = BuildAgentName(ExternalAgentType.ClaudeCode, pid, resolvedCwd),
            Type = ExternalAgentType.ClaudeCode,
            Status = status,
            Summary = summary,
            Pid = pid,
            ProjectPath = resolvedCwd,
            SessionId = session?.SessionId ?? sessionId ?? $"pid-{pid}",
            LastActive = session?.LastActive ?? (sessionFile != null ? File.GetLastWriteTime(sessionFile) : DateTime.Now),
            SessionFilePath = sessionFile,
        };
    }

    /// <summary>
    /// Encode CWD per Claude Code spec: every non-alphanumeric char → '-'.
    ///   /Users/foo/bar          → -Users-foo-bar
    ///   C:\Users\foo\bar        → C--Users-foo-bar
    ///   /Users/foo/my_project   → -Users-foo-my-project
    /// Lossy — multiple paths can collide. Caller must read session.cwd to disambiguate.
    /// </summary>
    private static string GetClaudeProjectDir(string cwd)
    {
        var sb = new StringBuilder(cwd.Length);
        foreach (var c in cwd)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        }
        return Path.Combine(ClaudeProjectsDir, sb.ToString());
    }

    private static string? ExtractResumeSessionId(string cmdLine)
    {
        if (string.IsNullOrWhiteSpace(cmdLine)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(
            cmdLine, @"--resume\s+([0-9a-f-]{36})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private sealed class PidFileEntry
    {
        public int Pid { get; set; }
        public string? SessionId { get; set; }
        public string? Cwd { get; set; }
        public long StartedAt { get; set; }
        public string? Status { get; set; }
        public string? WaitingFor { get; set; }

        public ExternalAgentStatus? MappedStatus => Status?.ToLowerInvariant() switch
        {
            // Claude Code writes "busy" while a turn is in flight; older/other
            // builds may write "running". Both mean actively working.
            "busy" or "running" or "processing" => ExternalAgentStatus.Running,
            // Awaiting user input / permission / a tool decision.
            "waiting" or "waiting_for_input" or "blocked" or "idle_waiting" => ExternalAgentStatus.Waiting,
            "idle" or "ready" or "done" => ExternalAgentStatus.Idle,
            _ => null,
        };
    }

    private static PidFileEntry? ReadMatchingPidFile(int pid, DateTime? procStartTime)
    {
        var pidFilePath = Path.Combine(ClaudeSessionsDir, $"{pid}.json");
        if (!File.Exists(pidFilePath)) return null;

        try
        {
            var json = File.ReadAllText(pidFilePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var entry = new PidFileEntry { Pid = pid };

            if (root.TryGetProperty("sessionId", out var sidEl))
                entry.SessionId = sidEl.GetString();
            if (root.TryGetProperty("cwd", out var cwdEl))
                entry.Cwd = cwdEl.GetString();
            if (root.TryGetProperty("startedAt", out var saEl) && saEl.ValueKind == JsonValueKind.Number)
                entry.StartedAt = saEl.GetInt64();
            if (root.TryGetProperty("status", out var stEl))
                entry.Status = stEl.GetString();
            if (root.TryGetProperty("waitingFor", out var wfEl))
                entry.WaitingFor = wfEl.GetString();

            // Validate: if process start time diverges by more than tolerance,
            // PID has been recycled and this file is stale.
            if (procStartTime is { } pst && entry.StartedAt > 0)
            {
                var fileTime = DateTimeOffset.FromUnixTimeMilliseconds(entry.StartedAt).LocalDateTime;
                var deltaMs = Math.Abs((pst - fileTime).TotalMilliseconds);
                if (deltaMs > PidFileStalenessMs)
                    return null;
            }

            return entry;
        }
        catch
        {
            return null;
        }
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    private ExternalAgentInfo DetectCodex(int pid, string cwd, DateTime? startTime)
    {
        var sessionFile = FindCodexSessionFile(cwd, startTime);

        var summary = "Codex session";
        var status = ExternalAgentStatus.Running;
        DateTime lastActive = DateTime.Now;
        string? sessionId = null;
        string? resolvedCwd = cwd;

        if (sessionFile != null)
        {
            var (sid, scwd, summ, lastTs, lastPayloadType) = ReadCodexSession(sessionFile);
            sessionId = sid;
            if (!string.IsNullOrWhiteSpace(scwd)) resolvedCwd = scwd;
            if (!string.IsNullOrWhiteSpace(summ)) summary = summ;
            if (lastTs != DateTime.MinValue) lastActive = lastTs;

            var diffMin = (DateTime.Now - lastActive).TotalMinutes;
            if (diffMin > IdleThresholdMinutes)
                status = ExternalAgentStatus.Idle;
            else if (lastPayloadType is "agent_message" or "task_complete" or "turn_aborted")
                status = ExternalAgentStatus.Waiting;
            else
                status = ExternalAgentStatus.Running;
        }

        return new ExternalAgentInfo
        {
            Name = BuildAgentName(ExternalAgentType.Codex, pid, resolvedCwd),
            Type = ExternalAgentType.Codex,
            Status = status,
            Summary = summary,
            Pid = pid,
            ProjectPath = resolvedCwd,
            SessionId = sessionId ?? $"pid-{pid}",
            LastActive = lastActive,
            SessionFilePath = sessionFile,
        };
    }

    private static string? FindCodexSessionFile(string cwd, DateTime? startTime)
    {
        if (!Directory.Exists(CodexSessionsDir)) return null;

        // Scan ±1 day window around process start
        var dayKeys = new HashSet<string>();
        var anchor = startTime ?? DateTime.Now;
        for (var offset = -1; offset <= 1; offset++)
        {
            var d = anchor.AddDays(offset);
            dayKeys.Add($"{d:yyyy}{Path.DirectorySeparatorChar}{d:MM}{Path.DirectorySeparatorChar}{d:dd}");
        }

        var candidates = new List<(string Path, string? SessionCwd)>();
        foreach (var dayKey in dayKeys)
        {
            var dir = Path.Combine(CodexSessionsDir, dayKey);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    var (_, scwd, _, _, _) = ReadCodexSession(file);
                    if (string.Equals(scwd, cwd, StringComparison.OrdinalIgnoreCase))
                        candidates.Add((file, scwd));
                }
            }
            catch { }
        }

        return candidates
            .OrderByDescending(c => File.GetLastWriteTime(c.Path))
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    private static (string? sessionId, string? cwd, string? summary, DateTime lastActive, string? lastPayloadType) ReadCodexSession(string filePath)
    {
        try
        {
            string? sessionId = null;
            string? cwd = null;
            string? lastMessage = null;
            string? lastPayloadType = null;
            DateTime lastActive = DateTime.MinValue;

            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0) return (null, null, null, DateTime.MinValue, null);

            // First line should be session_meta
            try
            {
                using var metaDoc = JsonDocument.Parse(lines[0]);
                var metaRoot = metaDoc.RootElement;
                if (metaRoot.TryGetProperty("type", out var tEl) && tEl.GetString() == "session_meta" &&
                    metaRoot.TryGetProperty("payload", out var pEl))
                {
                    if (pEl.TryGetProperty("id", out var idEl)) sessionId = idEl.GetString();
                    if (pEl.TryGetProperty("cwd", out var cEl)) cwd = cEl.GetString();
                }
            }
            catch { }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("timestamp", out var tsEl) &&
                        DateTime.TryParse(tsEl.GetString(), out var dt))
                    {
                        if (dt > lastActive) lastActive = dt;
                    }

                    if (root.TryGetProperty("payload", out var payloadEl))
                    {
                        if (payloadEl.TryGetProperty("type", out var ptEl))
                            lastPayloadType = ptEl.GetString();

                        if (payloadEl.TryGetProperty("message", out var msgEl) &&
                            msgEl.ValueKind == JsonValueKind.String)
                        {
                            var m = msgEl.GetString();
                            if (!string.IsNullOrWhiteSpace(m))
                                lastMessage = m;
                        }
                    }
                }
                catch { }
            }

            string? summary = lastMessage;
            if (summary != null && summary.Length > 120)
                summary = summary[..117] + "...";

            return (sessionId, cwd, summary, lastActive, lastPayloadType);
        }
        catch
        {
            return (null, null, null, DateTime.MinValue, null);
        }
    }

    // ── Gemini CLI ────────────────────────────────────────────────────────────

    private ExternalAgentInfo DetectGeminiCli(int pid, string cwd)
    {
        var sessionFile = FindGeminiSessionFile(cwd);

        var summary = "Gemini CLI session";
        var status = ExternalAgentStatus.Running;
        DateTime lastActive = DateTime.Now;
        string? sessionId = null;

        if (sessionFile != null)
        {
            var (sid, summ, lastTs) = ReadGeminiSessionMeta(sessionFile);
            sessionId = sid;
            if (!string.IsNullOrWhiteSpace(summ)) summary = summ;
            if (lastTs != DateTime.MinValue) lastActive = lastTs;

            var diffMin = (DateTime.Now - lastActive).TotalMinutes;
            if (diffMin > IdleThresholdMinutes)
                status = ExternalAgentStatus.Idle;
            else
                status = ExternalAgentStatus.Waiting;
        }

        return new ExternalAgentInfo
        {
            Name = BuildAgentName(ExternalAgentType.GeminiCli, pid, cwd),
            Type = ExternalAgentType.GeminiCli,
            Status = status,
            Summary = summary,
            Pid = pid,
            ProjectPath = cwd,
            SessionId = sessionId ?? $"pid-{pid}",
            LastActive = lastActive,
            SessionFilePath = sessionFile,
        };
    }

    private static string? FindGeminiSessionFile(string cwd)
    {
        if (!Directory.Exists(GeminiTmpDir) || string.IsNullOrWhiteSpace(cwd))
            return null;

        var cwdHash = Sha256Hex(cwd);

        try
        {
            // Scan every ~/.gemini/tmp/<shortId>/chats/session-*.json
            foreach (var shortIdDir in Directory.EnumerateDirectories(GeminiTmpDir))
            {
                var chatsDir = Path.Combine(shortIdDir, "chats");
                if (!Directory.Exists(chatsDir)) continue;

                foreach (var file in Directory.EnumerateFiles(chatsDir, "session-*.json")
                    .OrderByDescending(File.GetLastWriteTime))
                {
                    if (FileMatchesProjectHash(file, cwdHash))
                        return file;
                }
            }
        }
        catch { }

        return null;
    }

    private static bool FileMatchesProjectHash(string filePath, string cwdHash)
    {
        try
        {
            // Read just the first 4KB — projectHash sits near top of file
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(4096, fs.Length)];
            var read = fs.Read(buf, 0, buf.Length);
            var head = Encoding.UTF8.GetString(buf, 0, read);
            return head.Contains($"\"{cwdHash}\"", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static (string? sessionId, string? summary, DateTime lastActive) ReadGeminiSessionMeta(string filePath)
    {
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? sessionId = null;
            string? lastUser = null;
            DateTime lastActive = DateTime.MinValue;

            if (root.TryGetProperty("sessionId", out var sidEl))
                sessionId = sidEl.GetString();

            if (root.TryGetProperty("lastUpdated", out var luEl) &&
                DateTime.TryParse(luEl.GetString(), out var lu))
                lastActive = lu;

            if (root.TryGetProperty("messages", out var msgsEl) && msgsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in msgsEl.EnumerateArray())
                {
                    if (!msg.TryGetProperty("type", out var tEl)) continue;
                    if (tEl.GetString() != "user") continue;
                    if (!msg.TryGetProperty("content", out var cEl)) continue;

                    var text = ExtractGeminiPart(cEl);
                    if (!string.IsNullOrWhiteSpace(text))
                        lastUser = text.Length > 120 ? text[..117] + "..." : text;
                }
            }

            return (sessionId, lastUser, lastActive == DateTime.MinValue ? File.GetLastWriteTime(filePath) : lastActive);
        }
        catch
        {
            return (null, null, File.GetLastWriteTime(filePath));
        }
    }

    private static string ExtractGeminiPart(JsonElement contentEl)
    {
        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? "";

        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in contentEl.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var tEl))
                    return tEl.GetString() ?? "";
            }
        }

        return "";
    }

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // ── Generic / fallback ────────────────────────────────────────────────────

    private static ExternalAgentInfo BuildProcessOnlyAgent(ExternalAgentType type, int pid, string cwd)
    {
        return new ExternalAgentInfo
        {
            Name = BuildAgentName(type, pid, cwd),
            Type = type,
            Status = ExternalAgentStatus.Idle,
            Summary = $"{TypeLabel(type)} process",
            Pid = pid,
            ProjectPath = cwd,
            SessionId = $"pid-{pid}",
            LastActive = DateTime.Now,
            SessionFilePath = null,
        };
    }

    private static string GetAgentIdentityKey(ExternalAgentInfo agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.SessionFilePath))
            return $"{agent.Type}:{Path.GetFullPath(agent.SessionFilePath)}";

        if (!string.IsNullOrWhiteSpace(agent.ProjectPath))
            return $"{agent.Type}:{Path.GetFullPath(agent.ProjectPath)}";

        return $"{agent.Type}:pid:{agent.Pid}";
    }

    private static string BuildAgentName(ExternalAgentType type, int pid, string cwd)
    {
        var label = TypeLabel(type);
        var dir = string.IsNullOrWhiteSpace(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(dir) ? $"{label}-{pid}" : $"{dir} ({label})";
    }

    private static string TypeLabel(ExternalAgentType type) => type switch
    {
        ExternalAgentType.ClaudeCode => "claude",
        ExternalAgentType.GeminiCli  => "gemini",
        ExternalAgentType.Codex      => "codex",
        ExternalAgentType.OpenCode   => "opencode",
        ExternalAgentType.KiroCli    => "kiro",
        _                            => "agent",
    };

    private static bool IsNodeCliInvocation(string commandLine, string cliName)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        var normalized = commandLine.Replace('\\', '/');

        if (cliName == "claude")
        {
            if (normalized.Contains("/node_modules/@anthropic-ai/claude-code/", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("/node_modules/.bin/claude", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return normalized.Contains($"/node_modules/.bin/{cliName}", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"/{cliName}/cli", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"/{cliName}.js", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"/{cliName}.mjs", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($"/{cliName}.cmd", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains($" {cliName} ", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith($" {cliName}", StringComparison.OrdinalIgnoreCase);
    }

    // ── Conversation reading (consumed by AgentTeamPanelControl) ──────────────

    public List<ExternalAgentMessage> GetConversation(ExternalAgentInfo agent, int maxMessages = 50)
    {
        if (string.IsNullOrWhiteSpace(agent.SessionFilePath) || !File.Exists(agent.SessionFilePath))
            return [];

        return agent.Type switch
        {
            ExternalAgentType.GeminiCli => ReadGeminiConversation(agent.SessionFilePath, maxMessages),
            ExternalAgentType.Codex     => ReadCodexConversation(agent.SessionFilePath, maxMessages),
            _                           => _claudeParser.GetConversation(agent.SessionFilePath, maxMessages),
        };
    }

    public List<ExternalAgentMessage> GetNewMessages(ExternalAgentInfo agent, int skipCount)
    {
        // Read the full conversation (uncapped) so skipCount, which tracks the
        // total messages in the file at last render, stays aligned. Capping here
        // would shift the window and cause new messages to be missed or dupes
        // re-appended on long sessions.
        var all = GetConversation(agent, int.MaxValue);
        return all.Count > skipCount ? all.Skip(skipCount).ToList() : [];
    }

    private static List<ExternalAgentMessage> ReadCodexConversation(string filePath, int maxMessages)
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
                    if (!root.TryGetProperty("payload", out var pEl)) continue;
                    if (!pEl.TryGetProperty("type", out var tEl)) continue;
                    var pt = tEl.GetString() ?? "";
                    string role;
                    if (pt == "user_message") role = "user";
                    else if (pt is "agent_message" or "task_complete") role = "assistant";
                    else continue;

                    if (!pEl.TryGetProperty("message", out var mEl)) continue;
                    var content = mEl.GetString();
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    DateTime ts = DateTime.Now;
                    if (root.TryGetProperty("timestamp", out var tsEl) &&
                        DateTime.TryParse(tsEl.GetString(), out var dt))
                        ts = dt;

                    messages.Add(new ExternalAgentMessage
                    {
                        Role = role,
                        Content = content,
                        Timestamp = ts,
                    });
                }
                catch { }
            }
        }
        catch { }
        return messages.TakeLast(maxMessages).ToList();
    }

    private static List<ExternalAgentMessage> ReadGeminiConversation(string filePath, int maxMessages)
    {
        var messages = new List<ExternalAgentMessage>();
        try
        {
            var json = File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("messages", out var msgsEl) || msgsEl.ValueKind != JsonValueKind.Array)
                return messages;

            foreach (var msg in msgsEl.EnumerateArray())
            {
                if (!msg.TryGetProperty("type", out var tEl)) continue;
                var role = tEl.GetString() switch
                {
                    "user" => "user",
                    "gemini" or "assistant" or "model" => "assistant",
                    _ => null,
                };
                if (role == null) continue;

                if (!msg.TryGetProperty("content", out var cEl)) continue;
                var text = ExtractGeminiPart(cEl);
                if (string.IsNullOrWhiteSpace(text)) continue;

                DateTime ts = DateTime.Now;
                if (msg.TryGetProperty("timestamp", out var tsEl) &&
                    DateTime.TryParse(tsEl.GetString(), out var dt))
                    ts = dt;

                messages.Add(new ExternalAgentMessage
                {
                    Role = role,
                    Content = text,
                    Timestamp = ts,
                });
            }
        }
        catch { }
        return messages.TakeLast(maxMessages).ToList();
    }
}
