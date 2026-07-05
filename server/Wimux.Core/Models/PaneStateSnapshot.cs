using Wimux.Core.Terminal;

namespace Wimux.Core.Models;

public class PaneStateSnapshot
{
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    public PaneType Type { get; set; } = PaneType.Terminal;
    public string? WorkingDirectory { get; set; }
    public string? Shell { get; set; }
    public List<string> CommandHistory { get; set; } = [];
    public TerminalBufferSnapshot? BufferSnapshot { get; set; }
    public string? NotepadContent { get; set; }
    public string? NotepadFilePath { get; set; }
    public string? WebViewUrl { get; set; }
}
