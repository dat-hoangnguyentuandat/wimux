namespace Wimux.Core.Terminal;

/// <summary>
/// A single command block: the unit of terminal output in Warp's block model.
/// Populated incrementally as OSC 133 shell integration markers arrive.
///
/// Marker sequence:
///   OSC 133 ; A  — prompt start
///   OSC 133 ; B  — command start (end of prompt)
///   OSC 133 ; C  — output start (command submitted)
///   OSC 133 ; D [;exit_code]  — command end
/// </summary>
public sealed class CommandBlock
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    // Buffer row positions (scrollback-relative: negative = in scrollback)
    public int PromptRow { get; internal set; } = -1;
    public int CommandRow { get; internal set; } = -1;
    public int OutputStartRow { get; internal set; } = -1;
    public int OutputEndRow { get; internal set; } = -1;

    public string? CommandText { get; internal set; }
    public int? ExitCode { get; internal set; }

    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; internal set; }

    public bool HasPrompt => PromptRow >= 0;
    public bool HasCommand => CommandRow >= 0;
    public bool HasOutput => OutputStartRow >= 0;
    public bool IsRunning => HasOutput && OutputEndRow < 0;
    public bool IsComplete => OutputEndRow >= 0;

    public bool Succeeded => ExitCode == 0;
    public bool Failed => ExitCode.HasValue && ExitCode != 0;

    public TimeSpan? Duration => FinishedAt.HasValue ? FinishedAt.Value - StartedAt : null;
}

/// <summary>
/// Tracks command blocks by listening to OSC 133 shell integration markers
/// from a TerminalSession. Maintains an ordered list of blocks and fires
/// events when blocks are created or updated.
/// </summary>
public sealed class CommandBlockTracker
{
    private readonly List<CommandBlock> _blocks = [];
    private CommandBlock? _current;

    public IReadOnlyList<CommandBlock> Blocks => _blocks;

    public event Action<CommandBlock>? BlockStarted;
    public event Action<CommandBlock>? BlockUpdated;
    public event Action<CommandBlock>? BlockCompleted;

    /// <summary>
    /// Attaches this tracker to a terminal session's shell prompt marker events.
    /// </summary>
    public void Attach(TerminalSession session)
    {
        session.ShellPromptMarker += OnMarker;
    }

    public void Detach(TerminalSession session)
    {
        session.ShellPromptMarker -= OnMarker;
    }

    /// <summary>
    /// Called by the session with the current buffer cursor row whenever a
    /// shell integration marker fires. The row is the buffer row at the time
    /// the marker was received.
    /// </summary>
    public void OnMarkerWithRow(char marker, string? payload, int cursorRow)
    {
        switch (marker)
        {
            case 'A': // Prompt start — begin a new block
                _current = new CommandBlock { PromptRow = cursorRow };
                _blocks.Add(_current);
                BlockStarted?.Invoke(_current);
                break;

            case 'B': // Command start — end of prompt rendering
                if (_current == null)
                {
                    _current = new CommandBlock { PromptRow = cursorRow };
                    _blocks.Add(_current);
                    BlockStarted?.Invoke(_current);
                }
                _current.CommandRow = cursorRow;
                BlockUpdated?.Invoke(_current);
                break;

            case 'C': // Output start — command was submitted
                if (_current == null) break;
                _current.OutputStartRow = cursorRow;
                // Extract command text from payload if provided (some shells send it)
                if (!string.IsNullOrEmpty(payload) && _current.CommandText == null)
                    _current.CommandText = payload;
                BlockUpdated?.Invoke(_current);
                break;

            case 'D': // Command end — output finished
                if (_current == null) break;
                _current.OutputEndRow = cursorRow;
                _current.FinishedAt = DateTime.UtcNow;

                // Payload format: "exit_code" or empty
                if (!string.IsNullOrEmpty(payload) && int.TryParse(payload.TrimStart(';'), out int code))
                    _current.ExitCode = code;

                BlockCompleted?.Invoke(_current);
                BlockUpdated?.Invoke(_current);
                _current = null;
                break;
        }
    }

    // Overload used when row is not available (falls back to -1)
    private void OnMarker(char marker, string? payload) => OnMarkerWithRow(marker, payload, -1);

    /// <summary>
    /// Updates command text for the current in-progress block.
    /// Called by the session when it intercepts a submitted command line.
    /// </summary>
    public void SetCurrentCommandText(string commandText)
    {
        if (_current != null && string.IsNullOrEmpty(_current.CommandText))
        {
            _current.CommandText = commandText;
            BlockUpdated?.Invoke(_current);
        }
    }

    /// <summary>
    /// Returns the block whose output range contains the given buffer row, or null.
    /// </summary>
    public CommandBlock? BlockAtRow(int row)
    {
        for (int i = _blocks.Count - 1; i >= 0; i--)
        {
            var b = _blocks[i];
            int start = b.OutputStartRow >= 0 ? b.OutputStartRow : b.PromptRow;
            int end = b.OutputEndRow >= 0 ? b.OutputEndRow : int.MaxValue;
            if (row >= start && row <= end)
                return b;
        }
        return null;
    }

    /// <summary>
    /// Clears all blocks (e.g., on terminal clear).
    /// </summary>
    public void Clear()
    {
        _blocks.Clear();
        _current = null;
    }

    /// <summary>
    /// Returns the N most recent completed blocks.
    /// </summary>
    public IEnumerable<CommandBlock> RecentCompleted(int count = 10)
        => _blocks.Where(b => b.IsComplete).TakeLast(count);
}
