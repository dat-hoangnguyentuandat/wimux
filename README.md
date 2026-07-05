# Wimux

A Windows terminal workspace for running coding agents beside terminals,
browser panes, notifications, logs, and reusable project context.

Wimux gives you composable primitives instead of a prescriptive agent
workflow: workspaces, surfaces, split panes, ConPTY terminals, live browser
panes, command history, notification tracking, and an HTTP CLI that can automate
the running app.

## Highlights

### Agent-Friendly Workspaces

Create a workspace per project, keep multiple surfaces open inside it, and
split each surface into terminal, browser, and notepad panes. The sidebar keeps
project context visible with working directories, Git branch status, unread
notifications, and active pane metadata.

### Native Windows Terminals

Terminal panes run through Windows ConPTY, so shells and terminal applications
behave like they do in a normal Windows console. Recent output is buffered
server-side and replayed when a browser reconnects or the UI refreshes.

### Attention Signals

Wimux listens for terminal notification escape sequences and manual
notifications from the CLI. Unread state appears in the workspace/sidebar UI,
and the notification panel gives you a single place to jump back to sessions
that need input.

### Live Browser Panes

Split a real Edge or Chrome pane next to your terminal. Wimux launches a
dedicated browser profile through the Chrome DevTools Protocol, streams the tab
into the UI, and exposes browser state to the backend so agents can inspect and
act on web work without leaving the workspace.

### Built-In Agent Chat

The Agent Chat panel supports provider selection, streaming responses,
conversation threads, markdown rendering, context compaction, token accounting,
and custom providers. It can see workspace context and can call configured tools
when enabled.

### Command Memory

Command logs, terminal transcripts, snippets, command history, and workspace
templates turn repeated agent workflows into reusable building blocks. Sensitive
command content is scrubbed before storage where possible.

## Install

Install or update from the latest GitHub release with PowerShell:

```powershell
irm https://raw.githubusercontent.com/dat-hoangnguyentuandat/wimux/main/scripts/install.ps1 | iex
```

The installer downloads `wimux-win-x64.zip`, extracts it to:

```text
%LOCALAPPDATA%\Programs\wimux
```

and adds that folder to your user `PATH`.

Pin a release before running the installer:

```powershell
$env:WIMUX_VERSION = "v0.1.3"
irm https://raw.githubusercontent.com/dat-hoangnguyentuandat/wimux/main/scripts/install.ps1 | iex
```

After installation, open a new terminal:

```powershell
wimux
```

## Launcher

Running `wimux` opens the launcher menu:

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

Non-interactive launcher commands:

```powershell
wimux web       # start the host if needed and open the Web UI
wimux start     # start the host in the background
wimux stop      # stop the launcher-started host
wimux status    # print running or stopped
wimux cli       # open the interactive command console
wimux codex     # open a new surface running the codex CLI
wimux version   # print the launcher version
```

## Requirements

- Windows 10 or Windows 11.
- Edge or Chrome for live browser panes.
- For source builds: .NET 10 SDK and Node.js 20+.

The published release is self-contained and does not require installing .NET or
Node on the target machine.

## Architecture

```text
wimux/
  server/
    Wimux.Core/      terminal engine, models, settings, logs, agent services
    Wimux.Web/       ASP.NET Core host, REST API, WebSockets, static SPA
    Wimux.Cli/       HTTP automation CLI
    Wimux.Launcher/  Windows launcher and tray entry point
    Wimux.Tests/     backend and integration tests
  web/               React + TypeScript + xterm.js frontend
```

Runtime flow:

1. The launcher starts `wimux-web.exe` on `http://localhost:5201`.
2. The Web UI is served by the same ASP.NET Core host.
3. Terminal panes connect through `/ws/terminal/{paneId}`.
4. Keystrokes travel over WebSocket to a ConPTY-backed terminal session.
5. Browser panes use a dedicated Edge or Chrome profile controlled through CDP.
6. Persistent state is stored under `%LOCALAPPDATA%\wimux`.

## Features

### Workspaces and Surfaces

- Create, rename, select, duplicate, and close workspaces.
- Create multiple surfaces per workspace.
- Split panes vertically and horizontally.
- Equalize, zoom, and rearrange layouts.
- Persist layout and selected state across restarts.
- Per-workspace environment variables and SSH profile snippets.

### Terminal Experience

- Native ConPTY sessions.
- Shell detection for PowerShell and installed shells.
- Working directory tracking.
- Title, bell, and command marker support.
- Search within the active terminal.
- Broadcast input to all panes.
- Quick write into the focused pane.
- Right-click behavior tuned for mouse-aware terminal apps.

### Browser Experience

- Real Edge or Chrome tab streaming.
- Dedicated browser profile under Wimux app data.
- Back, forward, reload, close, and focus support.
- Session restore cleanup to avoid reopening stale hidden tabs.
- Network and cosmetic ad-block support for web panes.

### Agent Workflow

- Agent Chat panel with provider and model selector.
- OpenAI, Anthropic-compatible, Gemini-compatible, and custom providers.
- Streaming assistant messages.
- Conversation threads with search and delete.
- Context budgeting and compaction.
- Token and request quota dashboard.
- External agent session discovery and transcript parsing.
- Workspace-aware agent context from terminal and browser panes.

### Productivity Panels

- Command palette.
- Command logs by day with full-text search.
- Command history picker.
- Session Vault for captured terminal transcripts.
- Snippet manager with one-click terminal insertion.
- Workspace templates.
- Quick open fuzzy file finder.
- Notifications panel.
- Settings export and import.

## CLI

The automation CLI is published as `wimux-cli.exe` and talks to the running web
host over HTTP.

Default host:

```text
http://localhost:5201
```

Override it with:

```powershell
$env:WIMUX_URL = "http://localhost:5201"
```

Examples:

```powershell
wimux-cli status
wimux-cli notify --title "Agent" --body "Waiting for input"
wimux-cli workspace list
wimux-cli workspace create --name "My Project"
wimux-cli workspace select --index 0
wimux-cli surface create
wimux-cli split right
```

When developing from source:

```powershell
cd server/Wimux.Cli
dotnet run -- status
```

## Keyboard Shortcuts

### Workspaces

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` | New workspace |
| `Ctrl+B` | Toggle workspace sidebar |
| `F2` | Rename workspace |
| `Ctrl+Shift+W` | Close workspace |

### Surfaces and Panes

| Shortcut | Action |
| --- | --- |
| `Ctrl+T` | New surface |
| `Ctrl+W` | Close surface |
| `Ctrl+Tab` | Next surface |
| `Ctrl+Shift+Tab` | Previous surface |
| `Ctrl+D` | Split right |
| `Ctrl+Shift+D` | Split down |
| `Ctrl+Shift+Z` | Toggle pane zoom |
| `Ctrl+Alt+B` | Toggle broadcast input |

### Tools and Panels

| Shortcut | Action |
| --- | --- |
| `Ctrl+Shift+P` | Command palette |
| `Ctrl+P` | Quick open |
| `Ctrl+,` | Settings |
| `Ctrl+Shift+F` | Terminal search |
| `Ctrl+Alt+H` | Command history |
| `Ctrl+Shift+L` | Command logs |
| `Ctrl+Shift+V` | Session Vault |
| `Ctrl+Shift+S` | Snippets |
| `Ctrl+Shift+Q` | Quota dashboard |
| `Ctrl+Shift+A` | Agent sessions |
| `Ctrl+Shift+J` | Agent Chat |

## Session Restore and Storage

Wimux persists app-owned state, not arbitrary live process state.

Stored under `%LOCALAPPDATA%\wimux`:

- Workspace and surface layout.
- Settings and encrypted secrets.
- Agent conversation threads.
- Command logs and terminal transcripts.
- Snippets and workspace templates.
- Browser profile data.
- Launcher process metadata.

On restart, Wimux restores the layout, metadata, and saved terminal context it
owns. Shell processes, editors, terminal apps, and agent CLIs resume according
to their own durability features.

## Development

Run the backend host:

```powershell
cd server/Wimux.Web
dotnet run --urls http://localhost:5201
```

Run the frontend dev server:

```powershell
cd web
npm install
npm run dev
```

Open:

```text
http://localhost:5173
```

The dev server proxies `/api` and `/ws` to the backend on port `5201`.

Build and install from source:

```powershell
.\install.ps1
```

Create a release bundle:

```powershell
.\scripts\release.ps1
```

The release script creates:

```text
dist\wimux-win-x64.zip
```

## Security

Wimux exposes real local shells and browser automation through a local web host.
It is designed to bind to localhost for a trusted local user. Do not expose the
host to a network without adding authentication and transport protection.

API keys are stored through Windows DPAPI where secrets are used. Command logs
and transcript storage include best-effort scrubbing for obvious secret
patterns, but terminal output should still be treated as sensitive local data.

## FAQ

### Is Wimux a terminal or an agent orchestrator?

It is a terminal workspace with automation primitives. Agents are first-class
users of the workspace, but Wimux does not force one specific agent workflow.

### Does it replace a normal terminal?

It can, but it is most useful when you want terminals, browser panes,
notifications, logs, and agent chat in one workspace.

### Does it require a desktop app runtime?

No. The UI is browser-based and served by a local ASP.NET Core host. The
launcher and tray entry point are native Windows executables.

### Can it run without Edge or Chrome?

Terminal workspaces can run without them. Live browser panes need Edge or
Chrome because they use the Chrome DevTools Protocol.

### Can I use my own model provider?

Yes. Agent settings include built-in provider groups and custom providers with
configurable base URL, model, auth scheme, and secret name.

### Where do I report issues?

Use GitHub issues in this repository.
