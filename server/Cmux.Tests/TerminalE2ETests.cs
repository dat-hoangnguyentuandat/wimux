using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Cmux.Web.Services;
using FluentAssertions;
using Xunit;

namespace Cmux.Tests;

/// <summary>
/// Starts the real cmux-web Kestrel host as a child process and drives the
/// terminal over a real WebSocket, exactly as a browser would. This proves the
/// ConPTY-backed shell spawns, echoes input, and replays output on reconnect.
/// </summary>
public sealed class RealServerFixture : IAsyncLifetime
{
    private Process? _process;
    public int Port { get; private set; }
    public string BaseWs => $"ws://localhost:{Port}";

    public async Task InitializeAsync()
    {
        Port = GetFreePort();
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "Cmux.Web", "bin", "Debug", "net10.0-windows", "cmux-web.dll"));
        File.Exists(dll).Should().BeTrue($"web host must be built at {dll}");

        var stateDir = Path.Combine(Path.GetTempPath(), "cmux3-realsrv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDir);

        // IMPORTANT: do not redirect the child's stdout/stderr to pipes. ConPTY's
        // conhost inherits the launching process's std handles, and a redirected
        // pipe causes shell I/O to leak out of the PTY. Giving the child its own
        // (hidden) console keeps all terminal traffic on the WebSocket.
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "exec", dll, "--urls", $"http://localhost:{Port}" },
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        psi.Environment["LOCALAPPDATA"] = stateDir;
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";

        _process = Process.Start(psi)!;
        await WaitForPortAsync(Port, TimeSpan.FromSeconds(40));
    }

    public Task DisposeAsync()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("localhost", port);
                if (client.Connected)
                {
                    await Task.Delay(500); // let app fully start
                    return;
                }
            }
            catch { await Task.Delay(300); }
        }
        throw new TimeoutException($"cmux-web did not start on port {port}");
    }
}

public class TerminalE2ETests : IClassFixture<RealServerFixture>
{
    private readonly RealServerFixture _fx;
    public TerminalE2ETests(RealServerFixture fx) => _fx = fx;

    private static string Enc(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task SpawnsShell_AndEchoesCommandOutput()
    {
        var paneId = "rt-echo-" + Guid.NewGuid().ToString("N");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{_fx.BaseWs}/ws/terminal/{paneId}?cols=80&rows=24"), CancellationToken.None);

        var output = await SendUntilAsync(ws, "Write-Output ('CMUX'+'_RT_OK')\r", "CMUX_RT_OK", TimeSpan.FromSeconds(20));
        output.Should().Contain("CMUX_RT_OK");
    }

    [Fact]
    public async Task ReplaysBufferedOutput_OnReconnect()
    {
        var paneId = "rt-replay-" + Guid.NewGuid().ToString("N");
        var uri = new Uri($"{_fx.BaseWs}/ws/terminal/{paneId}?cols=80&rows=24");

        using (var first = new ClientWebSocket())
        {
            await first.ConnectAsync(uri, CancellationToken.None);
            var produced = await SendUntilAsync(first, "Write-Output ('REP'+'LAY_RT')\r", "REPLAY_RT", TimeSpan.FromSeconds(20));
            produced.Should().Contain("REPLAY_RT");
            await SafeClose(first);
        }

        await Task.Delay(400);

        using var second = new ClientWebSocket();
        await second.ConnectAsync(uri, CancellationToken.None);
        var replayed = await ReadUntilAsync(second, "REPLAY_RT", TimeSpan.FromSeconds(8));
        replayed.Should().Contain("REPLAY_RT");
        await SafeClose(second);
    }

    [Fact]
    public async Task Resize_DoesNotDropConnection()
    {
        var paneId = "rt-resize-" + Guid.NewGuid().ToString("N");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"{_fx.BaseWs}/ws/terminal/{paneId}?cols=80&rows=24"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("r100,40"), WebSocketMessageType.Text, true, CancellationToken.None);
        var output = await SendUntilAsync(ws, "Write-Output ('RES'+'IZE_OK')\r", "RESIZE_OK", TimeSpan.FromSeconds(20));
        output.Should().Contain("RESIZE_OK");
        ws.State.Should().Be(WebSocketState.Open);
    }

    [Fact]
    public async Task RawModeCli_ReceivesVietnameseUtf8Input()
    {
        var paneId = "rt-raw-vn-" + Guid.NewGuid().ToString("N");
        var js = "process.stdin.setRawMode(true);process.stdin.resume();let b=[];process.stdin.on('data',d=>{for(const x of d){if(x===13){const buf=Buffer.from(b);console.log('RAWHEX='+buf.toString('hex'));process.exit(0);}else b.push(x);}});";
        var shell = "node -e \"" + js + "\"";
        var uri = new Uri($"{_fx.BaseWs}/ws/terminal/{paneId}?cols=80&rows=24&shell={Uri.EscapeDataString(shell)}");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, CancellationToken.None);

        var text = "gõ tiếng Việt đậm dấu";
        await SendInputAsync(ws, text + "\r");
        var output = await ReadUntilAsync(ws, "RAWHEX=", TimeSpan.FromSeconds(20));

        var marker = "RAWHEX=";
        var idx = output.IndexOf(marker, StringComparison.Ordinal);
        idx.Should().BeGreaterThanOrEqualTo(0);
        var actualHex = new string(output[(idx + marker.Length)..]
            .TakeWhile(Uri.IsHexDigit)
            .ToArray());
        actualHex.Should().Be(Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant());
    }

    // ── helpers ─────────────────────────────────────────────
    // NOTE: cancelling ReceiveAsync with a per-iteration token aborts a .NET
    // ClientWebSocket, so we use one overall deadline token and a background
    // sender loop that re-sends the command until the marker arrives.
    private static async Task<string> SendUntilAsync(ClientWebSocket ws, string command, string marker, TimeSpan timeout)
    {
        var payload = Encoding.UTF8.GetBytes("i" + Enc(command));
        using var deadline = new CancellationTokenSource(timeout);
        var sender = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                try { await ws.SendAsync(payload, WebSocketMessageType.Text, true, deadline.Token); }
                catch { break; }
                try { await Task.Delay(1500, deadline.Token); }
                catch { break; }
            }
        });
        var result = await ReadUntilCoreAsync(ws, marker, deadline.Token);
        deadline.Cancel();
        try { await sender; } catch { /* ignore */ }
        return result;
    }

    private static async Task SendInputAsync(ClientWebSocket ws, string text)
    {
        var payload = Encoding.UTF8.GetBytes("i" + Enc(text));
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReadUntilAsync(ClientWebSocket ws, string marker, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        return await ReadUntilCoreAsync(ws, marker, deadline.Token);
    }

    private static async Task<string> ReadUntilCoreAsync(ClientWebSocket ws, string marker, CancellationToken token)
    {
        var sb = new StringBuilder();
        var frame = new StringBuilder();
        var buffer = new byte[16384];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                frame.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;
                var msg = frame.ToString(); frame.Clear();
                if (msg.Length > 0 && msg[0] == 'o')
                    sb.Append(Encoding.UTF8.GetString(Convert.FromBase64String(msg[1..])));
                if (sb.ToString().Contains(marker)) break;
            }
        }
        catch (OperationCanceledException) { /* deadline reached */ }
        return sb.ToString();
    }

    private static async Task SafeClose(ClientWebSocket ws)
    {
        try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch { /* peer gone */ }
    }
}
