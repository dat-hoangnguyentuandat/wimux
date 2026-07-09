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
    internal const string CurrentVersion = "0.1.5";

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var invokedAs = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? Environment.GetCommandLineArgs().FirstOrDefault() ?? "");
        if (string.Equals(invokedAs, "pwsh", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(invokedAs, "powershell", StringComparison.OrdinalIgnoreCase))
            return RunLauncher.OpenPowerShell(args);
        if (string.Equals(invokedAs, "wt", StringComparison.OrdinalIgnoreCase))
            return RunLauncher.OpenWindowsTerminal(args);

        // Allow "wimux web", "wimux cli", etc. as non-interactive shortcuts.
        if (args.Length > 0)
            return RunShortcut(args);

        if (!ServerManager.EnsureRunning())
            return 1;

        var stopped = false;
        void StopOwnedServer()
        {
            if (stopped)
                return;
            stopped = true;
            ServerManager.Stop();
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = false;
            StopOwnedServer();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopOwnedServer();

        try
        {
            var menu = new Menu();
            return menu.Run();
        }
        finally
        {
            StopOwnedServer();
        }
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
            case "cli":
                ServerManager.EnsureRunning();
                return InteractiveCli.Run();
            case "codex":
                ServerManager.EnsureRunning();
                return CodexLauncher.Open(args.Skip(1).ToArray());
            case "run":
                return RunLauncher.Open(args.Skip(1).ToArray());
            case "pwsh":
            case "powershell":
                return RunLauncher.OpenPowerShell(args.Skip(1).ToArray());
            case "wt":
                return RunLauncher.OpenWindowsTerminal(args.Skip(1).ToArray());
            case "update":
                return RunUpdate();
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
              wimux cli          Open the interactive CLI
              wimux codex [args] Open a wimux web terminal running `codex`
              wimux run -- <cmd> Open any command as a wimux terminal
              wimux pwsh [args]  Open PowerShell Core as a wimux terminal
              wimux wt [args]    Accept basic Windows Terminal args and open in wimux
              wimux update       Install the latest GitHub release if newer
              wimux version      Print version
            """);
    }

    private static int RunUpdate()
    {
        Console.WriteLine("Checking for updates...");
        var result = UpdateChecker.InstallLatest();
        if (result.UpToDate)
        {
            Console.WriteLine($"You are on the latest version (v{Program.CurrentVersion}).");
            return 0;
        }
        if (!result.Started)
        {
            Console.Error.WriteLine($"Update failed: {result.Error}");
            return 1;
        }
        Console.WriteLine($"Updating to v{result.Version}. The launcher will close and reopen when done.");
        return 0;
    }
}

