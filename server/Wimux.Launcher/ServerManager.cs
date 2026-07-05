using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace Wimux.Launcher;

/// <summary>
/// Owns the lifecycle of the wimux web host (Wimux.Web). The launcher always
/// makes sure a host is reachable before opening an interface.
/// </summary>
internal static class ServerManager
{
    internal const int Port = 5201;
    internal static string Url => $"http://localhost:{Port}";

    private static readonly string StateDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wimux", "launcher");

    private static string PidFile => Path.Combine(StateDir, "server.pid");

    private static readonly HttpClient Probe = new() { Timeout = TimeSpan.FromMilliseconds(800) };

    /// <summary>Quick TCP check: is something listening on the host port?</summary>
    internal static bool IsRunning()
    {
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect("127.0.0.1", Port, null, null);
            var ok = ar.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
            if (ok) { client.EndConnect(ar); return true; }
        }
        catch { /* not running */ }
        return false;
    }

    /// <summary>Confirms the host answers HTTP, not just an open socket.</summary>
    internal static bool IsHealthy()
    {
        try
        {
            using var resp = Probe.GetAsync($"{Url}/api/state").GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Starts the host if it is not already up, then waits for it.</summary>
    internal static bool EnsureRunning()
    {
        if (IsRunning())
            return true;

        var psi = BuildHostStartInfo();
        if (psi == null)
        {
            Console.Error.WriteLine("Could not locate the wimux web host (wimux-web or Wimux.Web project).");
            return false;
        }

        Console.WriteLine("Starting wimux server...");
        var proc = Process.Start(psi);
        if (proc != null)
            PersistPid(proc.Id);

        return WaitForUp(TimeSpan.FromSeconds(40));
    }

    private static bool WaitForUp(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning())
                return true;
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>Resolves how to start the host: published exe first, else dotnet run.</summary>
    private static ProcessStartInfo? BuildHostStartInfo()
    {
        var baseDir = AppContext.BaseDirectory;

        // 1) A published/copied host next to the launcher.
        foreach (var name in new[] { "wimux-web.exe", "Wimux.Web.exe" })
        {
            var exe = Path.Combine(baseDir, name);
            if (File.Exists(exe))
                return Hidden(new ProcessStartInfo(exe)
                {
                    Arguments = $"--urls {Url}",
                    WorkingDirectory = baseDir,
                });
        }

        // 2) Source checkout: walk up to find the web project.
        var csproj = FindWebProject(baseDir);
        if (csproj != null)
            return Hidden(new ProcessStartInfo("dotnet")
            {
                Arguments = $"run --project \"{csproj}\" --urls {Url}",
                WorkingDirectory = Path.GetDirectoryName(csproj),
            });

        return null;
    }

    private static string? FindWebProject(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "server", "Wimux.Web", "Wimux.Web.csproj");
            if (File.Exists(candidate))
                return candidate;
            // Also handle running from inside the server tree.
            var sibling = Path.Combine(dir.FullName, "Wimux.Web", "Wimux.Web.csproj");
            if (File.Exists(sibling))
                return sibling;
            dir = dir.Parent;
        }
        return null;
    }

    private static ProcessStartInfo Hidden(ProcessStartInfo psi)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        return psi;
    }

    private static void PersistPid(int pid)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(PidFile, pid.ToString());
        }
        catch { /* best effort */ }
    }

    /// <summary>Stops the host that this launcher started, if any.</summary>
    internal static bool Stop()
    {
        if (!IsRunning())
        {
            Console.WriteLine("wimux server is not running.");
            return true;
        }

        try
        {
            if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                try
                {
                    var proc = Process.GetProcessById(pid);
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                    File.Delete(PidFile);
                    Console.WriteLine("wimux server stopped.");
                    return true;
                }
                catch (ArgumentException) { /* pid gone */ }
            }

            Console.Error.WriteLine("Could not find a launcher-started server to stop. " +
                "The host may have been started elsewhere; close it manually.");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to stop server: {ex.Message}");
            return false;
        }
    }
}
