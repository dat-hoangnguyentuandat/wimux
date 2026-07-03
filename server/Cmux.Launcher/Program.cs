using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace Cmux.Launcher;

/// <summary>
/// cmux3 launcher. Run "cmux3" from any terminal to get an interactive menu
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

        // Allow "cmux3 web", "cmux3 cli", "cmux3 stop", etc. as non-interactive shortcuts.
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
                Console.WriteLine($"cmux3 server running at {ServerManager.Url}");
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
                Console.WriteLine($"cmux3 {CurrentVersion}");
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
            cmux3 - terminal workspace launcher

            Usage:
              cmux3              Open the interactive launcher menu
              cmux3 web          Start the host (if needed) and open the Web UI
              cmux3 start        Start the host in the background
              cmux3 stop         Stop the background host
              cmux3 status       Show whether the host is running
              cmux3 cli          Open the interactive CLI
              cmux3 codex [args] Open a cmux3 web terminal running `codex`
              cmux3 version      Print version
            """);
    }
}

