using System.Collections.Concurrent;
using System.Text;
using Cmux.Core.Models;
using Cmux.Core.Services;
using Cmux.Core.Terminal;

namespace Cmux.Web.Services;

/// <summary>
/// Owns all live ConPTY-backed terminal sessions for the web server.
/// Each pane (identified by paneId) maps to one TerminalSession.
/// Raw output bytes are fanned out to any subscribed WebSocket relays.
/// Terminal-side events (OSC notifications, shell prompt markers, cwd
/// changes) are wired into the shared Core services, mirroring the
/// desktop SurfaceViewModel behavior.
/// </summary>
public sealed class TerminalSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new();
    private readonly AppStateStore _store;
    private readonly NotificationService _notifications;
    private readonly CommandLogService _commandLog;
    private readonly CommandHistoryStore _history;

    public TerminalSessionManager(
        AppStateStore store,
        NotificationService notifications,
        CommandLogService commandLog,
        CommandHistoryStore history)
    {
        _store = store;
        _notifications = notifications;
        _commandLog = commandLog;
        _history = history;
    }

    private sealed class SessionEntry
    {
        public required TerminalSession Session { get; init; }
        public ConcurrentDictionary<Guid, Func<byte[], Task>> Subscribers { get; } = new();
        public ConcurrentDictionary<Guid, Func<TerminalEvent, Task>> EventSubscribers { get; } = new();
        public readonly List<byte> RecentOutput = new();
        public readonly Queue<TerminalInputTraceEntry> RecentInput = new();
        public readonly object OutputLock = new();
        public readonly object InputLock = new();
    }

    public record TerminalEvent(string Type, string PaneId, string? Data = null);
    public record TerminalInputTraceEntry(DateTimeOffset Timestamp, string Hex, string Text, string Escaped, int ByteCount);

    public bool Exists(string paneId) => _sessions.ContainsKey(paneId);

    public TerminalSession GetOrCreate(string paneId, int cols, int rows, string? cwd, string? command)
    {
        var entry = _sessions.GetOrAdd(paneId, id =>
        {
            var session = new TerminalSession(id, cols, rows);
            if (!string.IsNullOrWhiteSpace(cwd))
                session.WorkingDirectory = cwd;
            var newEntry = new SessionEntry { Session = session };

            session.RawOutputReceived += data =>
            {
                lock (newEntry.OutputLock)
                {
                    newEntry.RecentOutput.AddRange(data);
                    const int cap = 256 * 1024;
                    if (newEntry.RecentOutput.Count > cap)
                        newEntry.RecentOutput.RemoveRange(0, newEntry.RecentOutput.Count - cap);
                }
                foreach (var sub in newEntry.Subscribers.Values)
                    _ = sub(data);
            };

            session.TitleChanged += title => Emit(newEntry, new TerminalEvent("title", id, title));
            session.WorkingDirectoryChanged += dir => Emit(newEntry, new TerminalEvent("cwd", id, dir));
            session.BellReceived += () => Emit(newEntry, new TerminalEvent("bell", id));
            session.ProcessExited += () => Emit(newEntry, new TerminalEvent("exit", id));

            session.InputSubmitted += cmd =>
            {
                if (ShellWorkingDirectoryResolver.TryResolveCdCommand(cmd, session.WorkingDirectory, out var newCwd))
                {
                    session.WorkingDirectory = newCwd;
                    Emit(newEntry, new TerminalEvent("cwd", id, newCwd));
                }
            };

            session.NotificationReceived += (title, subtitle, body) =>
            {
                var ctx = _store.FindPaneContext(id);
                _notifications.AddNotification(
                    ctx?.Workspace.Id ?? "", ctx?.Surface.Id ?? "", id,
                    title, subtitle, body, NotificationSource.Osc9);
                Emit(newEntry, new TerminalEvent("notify", id, title));
            };

            session.ShellPromptMarker += (marker, payload) =>
            {
                var ctx = _store.FindPaneContext(id);
                _commandLog.HandlePromptMarker(
                    id, ctx?.Workspace.Id ?? "", ctx?.Surface.Id ?? "",
                    marker, payload, session.WorkingDirectory);

                if (marker == 'B')
                {
                    var sanitized = _commandLog.SanitizeCommandForStorage(payload);
                    if (!string.IsNullOrWhiteSpace(sanitized))
                        _history.Append(id, sanitized!);
                }
            };

            var ctxForStart = _store.FindPaneContext(id);
            var extraEnv = ctxForStart?.Workspace.EnvironmentVariables is { Count: > 0 } env
                ? new Dictionary<string, string>(env)
                : null;
            session.Start(command: command, workingDirectory: cwd, extraEnv: extraEnv);
            return newEntry;
        });

        return entry.Session;
    }

    private static void Emit(SessionEntry entry, TerminalEvent ev)
    {
        foreach (var sub in entry.EventSubscribers.Values)
            _ = sub(ev);
    }

    public byte[]? GetRecentOutput(string paneId)
    {
        if (!_sessions.TryGetValue(paneId, out var entry)) return null;
        lock (entry.OutputLock)
            return entry.RecentOutput.ToArray();
    }

    public Guid Subscribe(string paneId, Func<byte[], Task> onData, Func<TerminalEvent, Task> onEvent)
    {
        var id = Guid.NewGuid();
        if (_sessions.TryGetValue(paneId, out var entry))
        {
            entry.Subscribers[id] = onData;
            entry.EventSubscribers[id] = onEvent;
        }
        return id;
    }

    public void Unsubscribe(string paneId, Guid id)
    {
        if (_sessions.TryGetValue(paneId, out var entry))
        {
            entry.Subscribers.TryRemove(id, out _);
            entry.EventSubscribers.TryRemove(id, out _);
        }
    }

    public TerminalSession? Get(string paneId) =>
        _sessions.TryGetValue(paneId, out var entry) ? entry.Session : null;

    public void Write(string paneId, byte[] data)
    {
        if (!_sessions.TryGetValue(paneId, out var entry)) return;
        lock (entry.InputLock)
            AddInputTraceLocked(entry, data);
        entry.Session.Write(data);
    }

    private static void AddInputTraceLocked(SessionEntry entry, byte[] data)
    {
        var text = DecodeUtf8(data);
        var trace = new TerminalInputTraceEntry(
            DateTimeOffset.Now,
            Convert.ToHexString(data).ToLowerInvariant(),
            text,
            EscapeControlText(text),
            data.Length);

        entry.RecentInput.Enqueue(trace);
        while (entry.RecentInput.Count > 200)
            entry.RecentInput.Dequeue();
    }

    public IReadOnlyList<TerminalInputTraceEntry> GetInputTrace(string paneId)
    {
        if (!_sessions.TryGetValue(paneId, out var entry)) return [];
        lock (entry.InputLock)
            return entry.RecentInput.ToArray();
    }

    public void ClearInputTrace(string paneId)
    {
        if (!_sessions.TryGetValue(paneId, out var entry)) return;
        lock (entry.InputLock)
            entry.RecentInput.Clear();
    }

    private static string DecodeUtf8(byte[] data)
    {
        try { return Encoding.UTF8.GetString(data); }
        catch { return ""; }
    }

    private static string EscapeControlText(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '': sb.Append("<ESC>"); break;
                case '\b': sb.Append("<BS>"); break;
                case '': sb.Append("<DEL>"); break;
                case '\r': sb.Append("<CR>"); break;
                case '\n': sb.Append("<LF>"); break;
                case '\t': sb.Append("<TAB>"); break;
                default:
                    if (char.IsControl(ch))
                        sb.Append($"<0x{(int)ch:x2}>");
                    else
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    public void Resize(string paneId, int cols, int rows)
    {
        if (_sessions.TryGetValue(paneId, out var entry))
            entry.Session.Resize(cols, rows);
    }

    public string? CaptureTranscript(string paneId, string reason)
    {
        if (!_sessions.TryGetValue(paneId, out var entry)) return null;
        var ctx = _store.FindPaneContext(paneId);
        var text = entry.Session.Buffer.ExportPlainText();
        return _commandLog.SaveTerminalTranscript(
            ctx?.Workspace.Id ?? "", ctx?.Surface.Id ?? "", paneId,
            entry.Session.WorkingDirectory, text, reason);
    }

    public void Close(string paneId)
    {
        if (_sessions.TryRemove(paneId, out var entry))
        {
            entry.Session.Dispose();
            _history.Remove(paneId);
        }
    }

    public IReadOnlyCollection<string> ActivePanes => _sessions.Keys.ToArray();

    public void Dispose()
    {
        foreach (var entry in _sessions.Values)
            entry.Session.Dispose();
        _sessions.Clear();
    }
}
