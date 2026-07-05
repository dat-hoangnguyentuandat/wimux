using System.Diagnostics;

namespace Wimux.Launcher;

internal static class BrowserLauncher
{
    internal static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not open browser: {ex.Message}. URL: {url}");
        }
    }
}
