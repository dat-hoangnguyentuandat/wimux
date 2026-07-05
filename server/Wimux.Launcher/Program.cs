using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Wimux.Launcher;

/// <summary>
/// wimux launcher. Run "wimux" from any terminal to get an interactive menu
/// (inspired by 9router) for opening the Web UI, the interactive CLI, hiding
/// to the system tray, updating, or exiting. The launcher makes sure the web
/// host is running before handing control to any interface.
/// </summary>
internal static class Program
{
    internal const string CurrentVersion = "0.1.3";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Allow "wimux web", "wimux cli", "wimux stop", etc. as non-interactive shortcuts.
        if (args.Length > 0)
            return RunShortcut(args);

        var menu = new Menu();
        return menu.Run();
    }

    private static int RunShortcut(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        switch (cmd)
        {
            case "web":
            case "open":
                ServerManager.EnsureRunning();
                BrowserLauncher.Open(ServerManager.Url);
                return 0;
            case "start":
                ServerManager.EnsureRunning();
                Console.WriteLine($"wimux server running at {ServerManager.Url}");
                return 0;
            case "stop":
                return ServerManager.Stop() ? 0 : 1;
            case "status":
                Console.WriteLine(ServerManager.IsRunning()
                    ? $"running  {ServerManager.Url}"
                    : "stopped");
                return 0;
            case "cli":
                ServerManager.EnsureRunning();
                return InteractiveCli.Run();
            case "codex":
                ServerManager.EnsureRunning();
                return CodexLauncher.Open(args.Skip(1).ToArray());
            case "-v":
            case "--version":
            case "version":
                Console.WriteLine($"wimux {CurrentVersion}");
                return 0;
            case "-h":
            case "--help":
            case "help":
                PrintHelp();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown command: {cmd}");
                PrintHelp();
                return 1;
        }
    }

    internal static void PrintHelp()
    {
        Console.WriteLine("""
            wimux - terminal workspace launcher

            Usage:
              wimux              Open the interactive launcher menu
              wimux web          Start the host (if needed) and open the Web UI
              wimux start        Start the host in the background
              wimux stop         Stop the background host
              wimux status       Show whether the host is running
              wimux cli          Open the interactive CLI
              wimux codex [args] Open a wimux web terminal running `codex`
              wimux version      Print version
            """);
    }
}

