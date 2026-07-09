using System.Reflection;

namespace Wimux.Launcher;

/// <summary>
/// Interactive launcher menu (9router-style). Renders a boxed list of actions
/// and lets the user move with the arrow keys and select with Enter.
/// </summary>
internal sealed class Menu
{
    private readonly (string Label, string Hint, Func<bool> Action)[] _items;
    private int _selected;
    private string? _status;
    private bool _serverRunning;

    internal Menu()
    {
        _items = new (string, string, Func<bool>)[]
        {
            ("Web UI (Open in Browser)", "Open wimux in your browser",                   OpenWeb),
            ("CLI (Interactive)",        "Open the interactive wimux command console",      OpenCli),
            ("Check for Updates",        "Check GitHub for a newer wimux release",          CheckUpdate),
            ("Exit",                     "Quit the launcher",                               Exit),
        };
    }

    internal int Run()
    {
        // No interactive console (redirected stdin/stdout): cannot show the menu.
        // Fall back to printing help so the process does not crash with an unhandled exception.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Program.PrintHelp();
            return 0;
        }

        // Probe the server once up front; it is cached afterwards so moving
        // through the menu stays instant (the TCP probe can block for ~500ms).
        _serverRunning = ServerManager.IsRunning();

        // Kick off a non-blocking update check so the banner can show news.
        _ = Task.Run(() =>
        {
            var latest = UpdateChecker.CheckForUpdate();
            if (latest != null)
            {
                _status = $"Update available: v{latest} (current v{Program.CurrentVersion})";
                _dirty = true;
            }
        });

        Render();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow or ConsoleKey.K:
                    _selected = (_selected - 1 + _items.Length) % _items.Length;
                    _dirty = true;
                    break;
                case ConsoleKey.DownArrow or ConsoleKey.J:
                    _selected = (_selected + 1) % _items.Length;
                    _dirty = true;
                    break;
                case ConsoleKey.Enter:
                    if (!Invoke(_selected))
                        return 0;
                    break;
                case ConsoleKey.Escape or ConsoleKey.Q:
                    return 0;
                default:
                    if (char.IsDigit(key.KeyChar))
                    {
                        var idx = key.KeyChar - '1';
                        if (idx >= 0 && idx < _items.Length)
                        {
                            _selected = idx;
                            if (!Invoke(_selected))
                                return 0;
                        }
                    }
                    break;
            }

            if (_dirty)
                Render();
        }
    }


    /// <summary>Console.Clear throws on redirected/headless handles (e.g. when wimux is
    /// launched from a tool that captures stdout). Swallow that so the menu still runs.</summary>
    private static void SafeClear()
    {
        try { Console.Clear(); }
        catch (IOException) { }
    }
    private bool _dirty;

    /// <summary>Runs an action, then refreshes the cached server state once.</summary>
    private bool Invoke(int index)
    {
        SafeClear();
        var keepRunning = _items[index].Action();
        _serverRunning = ServerManager.IsRunning();
        _dirty = true;
        return keepRunning;
    }

    private void Render()
    {
        _dirty = false;
        SafeClear();
        var running = _serverRunning;
        var serverState = running ? "running" : "stopped";

        const int width = 64;
        var top = "+" + new string('-', width) + "+";

        WriteCentered("w i m u x", ConsoleColor.Cyan);
        Console.WriteLine();
        Console.WriteLine("  " + top);
        WriteBoxLine($"Terminal workspaces in your browser", width, ConsoleColor.Gray);
        WriteBoxLine($"server: {serverState}   url: {ServerManager.Url}", width,
            running ? ConsoleColor.Green : ConsoleColor.DarkGray);
        Console.WriteLine("  +" + new string('-', width) + "+");

        for (var i = 0; i < _items.Length; i++)
        {
            var selected = i == _selected;
            var pointer = selected ? " > " : "   ";
            var num = $"{i + 1}. ";
            var label = _items[i].Label;

            if (selected)
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.Cyan;
                Console.Write($"{pointer}{num}{label}".PadRight(width + 3));
                Console.ResetColor();
                Console.WriteLine();
            }
            else
            {
                Console.Write(pointer);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(num);
                Console.ResetColor();
                Console.WriteLine(label);
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  " + _items[_selected].Hint);
        Console.WriteLine();
        Console.WriteLine($"  Up/Down to move - Enter to select - 1-{_items.Length} quick keys - Esc to quit");
        if (_status != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  " + _status);
        }
        Console.ResetColor();
    }

    private static void WriteCentered(string text, ConsoleColor color)
    {
        var pad = Math.Max(0, (66 - text.Length) / 2);
        Console.ForegroundColor = color;
        Console.WriteLine();
        Console.WriteLine(new string(' ', pad) + text);
        Console.ResetColor();
    }

    private static void WriteBoxLine(string text, int width, ConsoleColor color)
    {
        if (text.Length > width - 2)
            text = text[..(width - 2)];
        Console.Write("  |");
        Console.ForegroundColor = color;
        Console.Write(" " + text.PadRight(width - 1));
        Console.ResetColor();
        Console.WriteLine("|");
    }

    // -- Actions (return true to stay in the menu) ---------------------

    private bool OpenWeb()
    {
        if (!ServerManager.EnsureRunning())
        {
            Pause("Failed to start the server.");
            return true;
        }
        BrowserLauncher.Open(ServerManager.Url);
        Pause($"Opened {ServerManager.Url} in your browser.");
        return true;
    }

    private bool OpenCli()
    {
        if (!ServerManager.EnsureRunning())
        {
            Pause("Failed to start the server.");
            return true;
        }
        InteractiveCli.Run();
        return true;
    }

    private bool CheckUpdate()
    {
        Console.WriteLine("Checking for updates...");
        var latest = UpdateChecker.CheckForUpdate();
        if (latest != null)
        {
            Console.WriteLine($"Update available: v{latest}. You have v{Program.CurrentVersion}.");
            Console.Write("Install now? [Y/n] ");
            var key = Console.ReadKey(intercept: true);
            Console.WriteLine();
            if (key.Key is ConsoleKey.N or ConsoleKey.Escape)
            {
                Pause("Update skipped.");
                return true;
            }

            Console.WriteLine("Downloading and preparing update...");
            var result = UpdateChecker.InstallLatest();
            if (result.Started)
            {
                Console.WriteLine($"Updating to v{result.Version}. This launcher will close and reopen when done.");
                Thread.Sleep(1200);
                return false;
            }
            Pause(result.UpToDate
                ? $"You are on the latest version (v{Program.CurrentVersion})."
                : $"Update failed: {result.Error}");
            return true;
        }

        Pause($"You are on the latest version (v{Program.CurrentVersion}).");
        return true;
    }

    private bool Exit()
    {
        Console.WriteLine("Bye.");
        return false;
    }

    private static void Pause(string? message)
    {
        if (message != null)
            Console.WriteLine(message);
        Console.WriteLine("\nPress any key to return to the menu...");
        Console.ReadKey(intercept: true);
    }
}


