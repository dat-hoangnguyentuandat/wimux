using System.Drawing;
using System.Windows.Forms;

namespace Wimux.Launcher;

/// <summary>
/// Hides the launcher to the Windows system tray while keeping the wimux host
/// running in the background. The tray menu mirrors the main launcher actions.
/// </summary>
internal static class Tray
{
    internal static void Run()
    {
        ServerManager.EnsureRunning();

        using var icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = $"wimux - {ServerManager.Url}",
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Web UI", null, (_, _) => BrowserLauncher.Open(ServerManager.Url));
        menu.Items.Add("Copy URL", null, (_, _) =>
        {
            try { Clipboard.SetText(ServerManager.Url); } catch { /* clipboard busy */ }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Stop server & Exit", null, (_, _) =>
        {
            ServerManager.Stop();
            icon.Visible = false;
            Application.Exit();
        });
        menu.Items.Add("Hide tray (keep server)", null, (_, _) =>
        {
            icon.Visible = false;
            Application.Exit();
        });

        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => BrowserLauncher.Open(ServerManager.Url);

        icon.ShowBalloonTip(2000, "wimux", $"Running in the background at {ServerManager.Url}",
            ToolTipIcon.Info);

        Application.Run();
    }
}
