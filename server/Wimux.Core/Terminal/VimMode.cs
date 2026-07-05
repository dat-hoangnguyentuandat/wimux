namespace Wimux.Core.Terminal;

public enum VimModeKind { Normal, Insert, Visual, Replace }

public enum VimMotion
{
    Left, Right, Up, Down,
    WordForward, WordBackward, WordEnd,
    BigWordForward, BigWordBackward, BigWordEnd,
    LineStart, LineEnd, LineFirstNonBlank,
    DocumentStart, DocumentEnd,
    FindCharForward, FindCharBackward,
    TillCharForward, TillCharBackward,
    RepeatFind, RepeatFindReverse,
    MatchingBracket,
    ParagraphForward, ParagraphBackward,
}

public enum VimOperator { Delete, Change, Yank, Indent, Outdent, ToLower, ToUpper }

public enum VimVisualType { Charwise, Linewise }

public enum VimActionKind
{
    // Mode changes
    EnterInsert,
    EnterInsertAtLineEnd,
    EnterInsertNewLineBelow,
    EnterInsertNewLineAbove,
    EnterInsertAtLineStart,
    EnterAppend,
    EnterVisual,
    EnterVisualLine,
    ExitToNormal,

    // Cursor motions (count applied)
    Move,

    // Edit operations
    OperatorMotion,   // e.g. dw, c$, y2j
    OperatorLine,     // dd, cc, yy
    OperatorVisual,   // d/c/y on visual selection

    // Single-char edits
    DeleteCharForward,
    DeleteCharBackward,
    ReplaceChar,
    ChangeToLineEnd,
    DeleteToLineEnd,

    // Clipboard
    Paste,
    PasteBefore,

    // Undo/Redo
    Undo,
    Redo,

    // Scroll
    ScrollHalfPageDown,
    ScrollHalfPageUp,

    // Search
    SearchForward,
    SearchBackward,
    RepeatSearch,
    RepeatSearchReverse,
}

public sealed record VimAction
{
    public VimActionKind Kind { get; init; }
    public int Count { get; init; } = 1;
    public VimMotion? Motion { get; init; }
    public VimOperator? Operator { get; init; }
    public char? Char { get; init; }
    public VimVisualType VisualType { get; init; }
    public char Register { get; init; } = '"';
}

/// <summary>
/// Finite-state automaton for Vim modal editing, ported from Warp's vim crate.
/// Call ProcessKey() with each keystroke; it returns a VimAction when a command is complete.
/// </summary>
public sealed class VimFsa
{
    public VimModeKind Mode { get; private set; } = VimModeKind.Normal;

    // Pending state
    private string _countBuffer = string.Empty;
    private string _operandCountBuffer = string.Empty;
    private VimOperator? _pendingOperator;
    private char _register = '"';
    private VimVisualType _visualType = VimVisualType.Charwise;

    // For f/F/t/T repetition
    private char _lastFindChar;
    private bool _lastFindForward;
    private bool _lastFindTill;

    // Dot repeat
    private VimAction? _dotRepeatAction;

    public event Action<VimModeKind>? ModeChanged;

    /// <summary>
    /// Process a key in the current mode. Returns a VimAction if a command is complete, else null.
    /// </summary>
    public VimAction? ProcessKey(VimKey key, char keyChar, VimModifierKeys modifiers)
    {
        return Mode switch
        {
            VimModeKind.Insert => ProcessInsertKey(key, keyChar, modifiers),
            VimModeKind.Replace => ProcessReplaceKey(key, keyChar),
            VimModeKind.Visual => ProcessVisualKey(key, keyChar, modifiers),
            _ => ProcessNormalKey(key, keyChar, modifiers),
        };
    }

    // ── Insert mode ──────────────────────────────────────────────────────────

    private VimAction? ProcessInsertKey(VimKey key, char keyChar, VimModifierKeys modifiers)
    {
        if (key == VimKey.Escape || (key == VimKey.LeftBracket && modifiers.HasFlag(VimModifierKeys.Control)))
        {
            SetMode(VimModeKind.Normal);
            return new VimAction { Kind = VimActionKind.ExitToNormal };
        }
        return null; // Let the terminal handle all other insert-mode keys normally
    }

    // ── Replace mode ─────────────────────────────────────────────────────────

    private VimAction? ProcessReplaceKey(VimKey key, char keyChar)
    {
        SetMode(VimModeKind.Normal);
        if (key == VimKey.Escape) return new VimAction { Kind = VimActionKind.ExitToNormal };
        if (keyChar != '\0')
            return new VimAction { Kind = VimActionKind.ReplaceChar, Char = keyChar };
        return new VimAction { Kind = VimActionKind.ExitToNormal };
    }

    // ── Visual mode ──────────────────────────────────────────────────────────

    private VimAction? ProcessVisualKey(VimKey key, char keyChar, VimModifierKeys modifiers)
    {
        if (key == VimKey.Escape)
        {
            SetMode(VimModeKind.Normal);
            return new VimAction { Kind = VimActionKind.ExitToNormal };
        }

        // Accumulate count
        if (keyChar is >= '1' and <= '9' && _countBuffer.Length == 0)
        {
            _countBuffer += keyChar;
            return null;
        }
        if (keyChar is >= '0' and <= '9' && _countBuffer.Length > 0)
        {
            _countBuffer += keyChar;
            return null;
        }

        int count = ConsumeCount();

        // Operators on visual selection
        VimOperator? op = keyChar switch
        {
            'd' or 'x' => VimOperator.Delete,
            'c' => VimOperator.Change,
            'y' => VimOperator.Yank,
            '>' => VimOperator.Indent,
            '<' => VimOperator.Outdent,
            'u' => VimOperator.ToLower,
            'U' => VimOperator.ToUpper,
            _ => null,
        };

        if (op.HasValue)
        {
            SetMode(VimModeKind.Normal);
            return Repeatable(new VimAction
            {
                Kind = VimActionKind.OperatorVisual,
                Operator = op.Value,
                VisualType = _visualType,
                Register = _register,
                Count = count,
            });
        }

        // Toggle visual type
        if (keyChar == 'v')
        {
            if (_visualType == VimVisualType.Charwise) { SetMode(VimModeKind.Normal); return new VimAction { Kind = VimActionKind.ExitToNormal }; }
            _visualType = VimVisualType.Charwise;
            return null;
        }
        if (keyChar == 'V')
        {
            if (_visualType == VimVisualType.Linewise) { SetMode(VimModeKind.Normal); return new VimAction { Kind = VimActionKind.ExitToNormal }; }
            _visualType = VimVisualType.Linewise;
            return null;
        }

        // Motion in visual mode
        var motion = ParseMotion(key, keyChar, modifiers);
        if (motion.HasValue)
            return new VimAction { Kind = VimActionKind.Move, Motion = motion.Value, Count = count };

        return null;
    }

    // ── Normal mode ──────────────────────────────────────────────────────────

    private VimAction? ProcessNormalKey(VimKey key, char keyChar, VimModifierKeys modifiers)
    {
        bool ctrl = modifiers.HasFlag(VimModifierKeys.Control);

        // Find char target — must be checked FIRST before any other key handling
        if (_pendingFindMotion != '\0' && keyChar != '\0')
        {
            var findMotion = _pendingFindMotion switch
            {
                'f' => VimMotion.FindCharForward,
                'F' => VimMotion.FindCharBackward,
                't' => VimMotion.TillCharForward,
                _ => VimMotion.TillCharBackward,
            };
            _lastFindChar = keyChar;
            _lastFindForward = _pendingFindMotion is 'f' or 't';
            _lastFindTill = _pendingFindMotion is 't' or 'T';
            _pendingFindMotion = '\0';

            int count = ConsumeCount();
            if (_pendingOperator.HasValue)
            {
                var op = _pendingOperator.Value;
                _pendingOperator = null;
                return Repeatable(new VimAction { Kind = VimActionKind.OperatorMotion, Operator = op, Motion = findMotion, Char = keyChar, Count = count, Register = _register });
            }
            return new VimAction { Kind = VimActionKind.Move, Motion = findMotion, Char = keyChar, Count = count };
        }

        // Register selection: "x
        if (keyChar == '"')
        {
            // Next key sets register — handled by a flag; simplify: just track it
            // For now we handle it inline on next call via a small state
            _pendingRegisterSelect = true;
            return null;
        }
        if (_pendingRegisterSelect)
        {
            _pendingRegisterSelect = false;
            if (char.IsLetterOrDigit(keyChar) || keyChar == '"' || keyChar == '+' || keyChar == '*')
                _register = keyChar;
            return null;
        }

        // Dot repeat
        if (keyChar == '.' && _dotRepeatAction != null)
        {
            int count = ConsumeCount();
            return _dotRepeatAction with { Count = count > 1 ? count : _dotRepeatAction.Count };
        }

        // Undo / Redo
        if (keyChar == 'u') return new VimAction { Kind = VimActionKind.Undo, Count = ConsumeCount() };
        if (ctrl && key == VimKey.R) return new VimAction { Kind = VimActionKind.Redo, Count = ConsumeCount() };

        // Scroll
        if (ctrl && key == VimKey.D) return new VimAction { Kind = VimActionKind.ScrollHalfPageDown };
        if (ctrl && key == VimKey.U) return new VimAction { Kind = VimActionKind.ScrollHalfPageUp };

        // Search
        if (keyChar == '/') return new VimAction { Kind = VimActionKind.SearchForward };
        if (keyChar == '?') return new VimAction { Kind = VimActionKind.SearchBackward };
        if (keyChar == 'n') return new VimAction { Kind = VimActionKind.RepeatSearch };
        if (keyChar == 'N') return new VimAction { Kind = VimActionKind.RepeatSearchReverse };

        // Paste
        if (keyChar == 'p') return Repeatable(new VimAction { Kind = VimActionKind.Paste, Register = _register, Count = ConsumeCount() });
        if (keyChar == 'P') return Repeatable(new VimAction { Kind = VimActionKind.PasteBefore, Register = _register, Count = ConsumeCount() });

        // Count accumulation
        if (keyChar is >= '1' and <= '9' && _countBuffer.Length == 0 && _pendingOperator == null)
        {
            _countBuffer += keyChar;
            return null;
        }
        if (keyChar is >= '0' and <= '9' && _countBuffer.Length > 0 && _pendingOperator == null)
        {
            _countBuffer += keyChar;
            return null;
        }
        // Operand count (after operator)
        if (keyChar is >= '1' and <= '9' && _pendingOperator != null && _operandCountBuffer.Length == 0)
        {
            _operandCountBuffer += keyChar;
            return null;
        }
        if (keyChar is >= '0' and <= '9' && _pendingOperator != null && _operandCountBuffer.Length > 0)
        {
            _operandCountBuffer += keyChar;
            return null;
        }

        // Mode transitions
        if (keyChar == 'i') { SetMode(VimModeKind.Insert); return new VimAction { Kind = VimActionKind.EnterInsert }; }
        if (keyChar == 'a') { SetMode(VimModeKind.Insert); return new VimAction { Kind = VimActionKind.EnterAppend }; }
        if (keyChar == 'I') { SetMode(VimModeKind.Insert); return new VimAction { Kind = VimActionKind.EnterInsertAtLineStart }; }
        if (keyChar == 'A') { SetMode(VimModeKind.Insert); return new VimAction { Kind = VimActionKind.EnterInsertAtLineEnd }; }
        if (keyChar == 'o') { SetMode(VimModeKind.Insert); return Repeatable(new VimAction { Kind = VimActionKind.EnterInsertNewLineBelow }); }
        if (keyChar == 'O') { SetMode(VimModeKind.Insert); return Repeatable(new VimAction { Kind = VimActionKind.EnterInsertNewLineAbove }); }
        if (keyChar == 'v') { SetMode(VimModeKind.Visual); _visualType = VimVisualType.Charwise; return new VimAction { Kind = VimActionKind.EnterVisual }; }
        if (keyChar == 'V') { SetMode(VimModeKind.Visual); _visualType = VimVisualType.Linewise; return new VimAction { Kind = VimActionKind.EnterVisualLine }; }
        if (keyChar == 'r') { SetMode(VimModeKind.Replace); return null; }

        // Single-char edits
        if (keyChar == 'x') return Repeatable(new VimAction { Kind = VimActionKind.DeleteCharForward, Count = ConsumeCount(), Register = _register });
        if (keyChar == 'X') return Repeatable(new VimAction { Kind = VimActionKind.DeleteCharBackward, Count = ConsumeCount(), Register = _register });
        if (keyChar == 'C') { SetMode(VimModeKind.Insert); return Repeatable(new VimAction { Kind = VimActionKind.ChangeToLineEnd, Register = _register }); }
        if (keyChar == 'D') return Repeatable(new VimAction { Kind = VimActionKind.DeleteToLineEnd, Register = _register });
        if (keyChar == 's') { SetMode(VimModeKind.Insert); return Repeatable(new VimAction { Kind = VimActionKind.OperatorMotion, Operator = VimOperator.Change, Motion = VimMotion.Right, Count = ConsumeCount(), Register = _register }); }

        // Find char motions
        if (keyChar is 'f' or 'F' or 't' or 'T')
        {
            _pendingFindMotion = keyChar;
            return null;
        }

        // Repeat find
        if (keyChar == ';') return new VimAction { Kind = VimActionKind.Move, Motion = VimMotion.RepeatFind, Count = ConsumeCount() };
        if (keyChar == ',') return new VimAction { Kind = VimActionKind.Move, Motion = VimMotion.RepeatFindReverse, Count = ConsumeCount() };

        // Operator pending
        if (keyChar is 'd' or 'c' or 'y' or '>' or '<')
        {
            var op = keyChar switch
            {
                'd' => VimOperator.Delete,
                'c' => VimOperator.Change,
                'y' => VimOperator.Yank,
                '>' => VimOperator.Indent,
                _ => VimOperator.Outdent,
            };

            if (_pendingOperator == op)
            {
                // Double operator: dd, cc, yy
                _pendingOperator = null;
                int count = ConsumeCount();
                if (op == VimOperator.Change) SetMode(VimModeKind.Insert);
                return Repeatable(new VimAction { Kind = VimActionKind.OperatorLine, Operator = op, Count = count, Register = _register });
            }

            _pendingOperator = op;
            return null;
        }

        // Motion (possibly completing an operator)
        var motion2 = ParseMotion(key, keyChar, modifiers);
        if (motion2.HasValue)
        {
            int count = ConsumeCount();
            if (_pendingOperator.HasValue)
            {
                var op = _pendingOperator.Value;
                _pendingOperator = null;
                if (op == VimOperator.Change) SetMode(VimModeKind.Insert);
                return Repeatable(new VimAction { Kind = VimActionKind.OperatorMotion, Operator = op, Motion = motion2.Value, Count = count, Register = _register });
            }
            return new VimAction { Kind = VimActionKind.Move, Motion = motion2.Value, Count = count };
        }

        // Unknown key — clear pending state
        _pendingOperator = null;
        _countBuffer = string.Empty;
        _operandCountBuffer = string.Empty;
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool _pendingRegisterSelect;
    private char _pendingFindMotion;

    private VimMotion? ParseMotion(VimKey key, char keyChar, VimModifierKeys modifiers)
    {
        bool ctrl = modifiers.HasFlag(VimModifierKeys.Control);

        return keyChar switch
        {
            'h' => VimMotion.Left,
            'l' => VimMotion.Right,
            'k' => VimMotion.Up,
            'j' => VimMotion.Down,
            'w' => VimMotion.WordForward,
            'b' => VimMotion.WordBackward,
            'e' => VimMotion.WordEnd,
            'W' => VimMotion.BigWordForward,
            'B' => VimMotion.BigWordBackward,
            'E' => VimMotion.BigWordEnd,
            '0' => VimMotion.LineStart,
            '$' => VimMotion.LineEnd,
            '^' => VimMotion.LineFirstNonBlank,
            '%' => VimMotion.MatchingBracket,
            '{' => VimMotion.ParagraphBackward,
            '}' => VimMotion.ParagraphForward,
            _ => key switch
            {
                VimKey.Left => VimMotion.Left,
                VimKey.Right => VimMotion.Right,
                VimKey.Up => VimMotion.Up,
                VimKey.Down => VimMotion.Down,
                VimKey.Home => VimMotion.LineStart,
                VimKey.End => VimMotion.LineEnd,
                _ => null,
            }
        };
    }

    private int ConsumeCount()
    {
        int actionCount = _countBuffer.Length > 0 && int.TryParse(_countBuffer, out int ac) ? ac : 1;
        int operandCount = _operandCountBuffer.Length > 0 && int.TryParse(_operandCountBuffer, out int oc) ? oc : 1;
        _countBuffer = string.Empty;
        _operandCountBuffer = string.Empty;
        return Math.Max(1, actionCount * operandCount);
    }

    private VimAction Repeatable(VimAction action)
    {
        _dotRepeatAction = action;
        return action;
    }

    private void SetMode(VimModeKind mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ModeChanged?.Invoke(mode);
    }

    public void Reset()
    {
        _countBuffer = string.Empty;
        _operandCountBuffer = string.Empty;
        _pendingOperator = null;
        _pendingRegisterSelect = false;
        _pendingFindMotion = '\0';
        _register = '"';
        SetMode(VimModeKind.Normal);
    }
}

// Minimal key/modifier enums so Wimux.Core has no WPF dependency
public enum VimKey
{
    None,
    Escape, Enter, Tab, Backspace, Delete, Space,
    Left, Right, Up, Down, Home, End, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    OemOpenBrackets, LeftBracket,
}

[Flags]
public enum VimModifierKeys
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}
