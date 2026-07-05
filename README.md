# wimux

Web port of wimux-windows. The native WPF desktop app is replaced by a
browser-based UI, while the proven ConPTY terminal engine (`Wimux.Core`) is
reused unchanged on the server.

## Architecture

```text
wimux/
  server/
    Wimux.Core/   reused terminal engine (ConPTY + VT parser + buffer + OSC)
    Wimux.Web/    ASP.NET Core host: REST API + terminal WebSocket relay + static SPA
  web/           React + TypeScript + xterm.js frontend (Vite)
```

- The browser renders terminals with xterm.js.
- Keystrokes travel over a WebSocket to `Wimux.Web`, which feeds them to a
  real ConPTY-backed `TerminalSession` on Windows.
- Raw shell output streams back over the same socket; recent output is
  buffered server-side so a page refresh replays the screen.
- Workspaces, surfaces (tabs) and split-pane layouts are stored as JSON in
  `%LOCALAPPDATA%\wimux\state.json`.

## Launcher (`wimux`)

The fastest way in: run `wimux` from any terminal (PowerShell, Windows Terminal,
cmd) to open an interactive launcher menu — pick an interface and go.

```text
   w i m u x

  +----------------------------------------------------------------+
  | Terminal workspaces in your browser                            |
  | server: running   url: http://localhost:5201                   |
  +----------------------------------------------------------------+
 > 1. Web UI (Open in Browser)
   2. CLI (Interactive)
   3. Run in Background (Tray)
   4. Server: Start / Stop
   5. Check for Updates
   6. Exit
```

- Move with the arrow keys (or `j`/`k`), select with `Enter`, or press `1`-`6`.
- Web UI starts the host if needed and opens `http://localhost:5201`.
- Run in Background hides wimux to the system tray with the server still up.
- The launcher checks GitHub for newer releases in the background.

### Install

Install (or update) with one line in PowerShell — no .NET or Node needed,
it downloads a self-contained build from the latest GitHub release:

```powershell
irm https://raw.githubusercontent.com/dat-hoangnguyentuandat/wimux/main/scripts/install.ps1 | iex
```

This drops wimux into `%LOCALAPPDATA%\Programs\wimux` and adds it to your PATH.
Pin a version with `$env:WIMUX_VERSION="v0.1.0"` before running.

Building from a source checkout instead? Run `./install.ps1` from the repo root
(needs the .NET SDK + Node).

Then open a new terminal and run `wimux`. Non-interactive shortcuts are also
available for scripting:

```powershell
wimux web       # start host (if needed) + open browser
wimux start     # start host in the background
wimux stop      # stop the background host
wimux status    # running / stopped
wimux cli       # interactive wimux command console
```

## Requirements

- Windows 10/11 (ConPTY is Windows-only).
- .NET 10 SDK.
- Node.js 20+ / npm.

## Develop

Run the backend (terminal host + API) on port 5201:

```powershell
cd server/Wimux.Web
dotnet run --urls http://localhost:5201
```

Run the frontend dev server (proxies /api and /ws to the backend) on 5173:

```powershell
cd web
npm install
npm run dev
```

Open http://localhost:5173.

## Production build

Build the SPA into the backend's `wwwroot`, then run the single web host:

```powershell
cd web
npm run build
cd ../server/Wimux.Web
dotnet run --urls http://localhost:5201
```

Open http://localhost:5201 — the API, WebSocket, and UI are all served from
one process.

> Security note: the server exposes real shells with no authentication and
> binds to localhost only. Do not expose it to a network without adding auth.

## Features ported

- Workspaces (create, select, rename, close) — `Ctrl+N`
- Surfaces / tabs (create, select, rename, close) — `Ctrl+T`
- Split panes, vertical and horizontal, with draggable dividers — `Ctrl+D`, `Ctrl+Shift+D`
- Native ConPTY terminal sessions per pane, with title/cwd/bell events
- Terminal output replay on reconnect/refresh
- Command palette — `Ctrl+Shift+P`
- Settings (theme, font family, font size) — `Ctrl+,`
- Built-in terminal color themes from the original app
- Sidebar toggle — `Ctrl+B`
- Session layout persistence across restarts
- Terminal notifications via OSC 9/777 + sidebar/tabbar unread badge — `Ctrl+I`
- Command logs (per-day + full-text search), powered by shell prompt markers — `Ctrl+Shift+L`
- Command history picker for the focused pane — `Ctrl+Alt+H`
- Session Vault: browse captured terminal transcripts — `Ctrl+Shift+V`
- Snippets manager with insert-into-terminal — `Ctrl+Shift+S`
- Agent quota dashboard (tokens/requests per provider/model) — `Ctrl+Shift+Q`
- Workspace templates + git branch/remote + listening-port detection APIs
- Workspace shortcuts: jump `Ctrl+1..9`, rename `Ctrl+Shift+R`, close `Ctrl+Shift+W`
- Surface cycling — `Ctrl+Shift+]` / `Ctrl+Shift+[`
- `wimux-cli` CLI (HTTP) for automation: `notify`, `workspace`, `surface`, `split`, `status`
- Web and notepad pane types alongside terminals (per-pane type switcher) — auto-saved notes
- External AI agent detection + conversation viewer (Claude/Codex/Gemini/etc.) — `Ctrl+Shift+A`
- Per-workspace environment variables (injected into new terminals) + SSH profiles — `Ctrl+Shift+E`
- Knowledge-graph API + interactive graph view — `Ctrl+Shift+G`
- Source tree browser with file preview — `Ctrl+Shift+O`
- Broadcast input to all panes — `Ctrl+Alt+B`
- In-terminal search — `Ctrl+Shift+F`
- Quick write in the focused terminal — `Shift+W`
- Pane focus navigation (`Ctrl+Alt+Arrow`) and zoom (`Ctrl+Shift+Z`)
- Workspace templates browser — `Ctrl+Shift+T`
- Built-in AI agent runtime (OpenAI/Anthropic/Gemini-compatible) with streaming chat — `Ctrl+Shift+J`
- Agent settings (provider, model, API key, system prompt, tools) and the agent `wimux` tool bridge
- Agent conversation thread store with markdown-rendered messages
- Full settings (appearance/terminal/behavior) with JSON export/import
- UI themes: Dark+ / Light / High Contrast
- Ad block toggle for web panes (network-level blocking is desktop/WebView2-only)
- Quick open fuzzy file finder — `Ctrl+P`
- Custom terminal colors (background/foreground/cursor/selection)
- Save current workspace layout as a reusable template, and apply templates to spawn new workspaces
- Per-workspace sidebar status: git branch + unread count

## CLI

The `wimux-cli` CLI talks to the running web host over HTTP (default
`http://localhost:5201`, override with `WIMUX_URL`).

```powershell
cd server/Wimux.Cli
dotnet run -- status
dotnet run -- notify --title "Claude Code" --body "Waiting for input"
dotnet run -- workspace list
dotnet run -- workspace create --name "My Project"
dotnet run -- workspace select --index 0
dotnet run -- surface create
dotnet run -- split right
```








