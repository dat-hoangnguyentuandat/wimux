import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import { DockviewReact, DockviewDefaultTab, type DockviewApi, type DockviewReadyEvent, type IDockviewHeaderActionsProps, type IDockviewPanelProps, type IDockviewPanelHeaderProps } from "dockview";
import "dockview/dist/styles/dockview.css";
import { SplitView } from "./SplitView";
import { AgentChatPanel } from "./AgentChatPanel";
import { CommandLogsPanel } from "./CommandLogsPanel";
import { SessionVaultPanel } from "./SessionVaultPanel";
import { QuotaPanel } from "./QuotaPanel";
import { AgentsPanel } from "./AgentsPanel";
import { WorkspaceSettingsPanel } from "./WorkspaceSettingsPanel";
import { useAppDialog } from "./AppDialog";
import { api } from "../lib/api";
import type { AppState, SplitNode, Surface, TerminalTheme, Workspace } from "../lib/api";
import { BellIcon, PlusIcon, XIcon } from "./icons";

export type PanelId = "sidebar" | "agentChat" | "logs" | "vault" | "quota" | "agents" | "wsSettings";
export const DOCK_LAYOUT_STORAGE_KEY = "wimux.dockLayout.v4";

export const DOCK_PANELS: { id: PanelId; label: string }[] = [
  { id: "sidebar", label: "Workspaces" },
  { id: "agentChat", label: "Agent Chat" },
  { id: "logs", label: "Command Logs" },
  { id: "vault", label: "Session Vault" },
  { id: "quota", label: "Quota" },
  { id: "agents", label: "AI Agents" },
  { id: "wsSettings", label: "SSH / Env" },
];

// Shared context so dockview-hosted panels can read live app state/handlers.
interface DockCtx {
  state: AppState;
  workspace?: Workspace;
  surface?: Surface;
  zoomedPaneId: string | null;
  focusedPaneId?: string;
  focusedCwd: string;
  theme?: TerminalTheme;
  fontFamily: string;
  fontSize: number;
  customColors?: { background?: string; foreground?: string; cursor?: string; selection?: string };
  settings?: any;
  searchOpen: boolean;
  setSearchOpen: (v: boolean) => void;
  refresh: () => void;
  refreshUnread: () => void;
  insertIntoFocused: (text: string) => void;
  focusPane: (id: string) => void;
  closePane: (id: string) => void;
  setPaneTitle: (id: string, t: string) => void;
  setPaneCwd: (id: string, c: string) => void;
  onTerminalNotify: () => void;
  setPaneType: (id: string, t: string) => void;
  setRatio: (id: string, r: number) => void;
  splitPane: (dir: "vertical" | "horizontal") => void;
  toggleZoom: () => void;
  newSurface: () => void;
  selectSurface: (id: string) => void;
  closeSurface: (id: string) => void;
  closePanel: (id: PanelId) => void;
  openPanel: (id: PanelId) => void;
  selectWorkspace: (id: string) => void;
  closeWorkspace: (id: string) => void;
  newWorkspace: () => void;
  workspaceRenameRequest?: { id: string; token: number } | null;
  wsStatus: Record<string, { branch?: string; workingDirectory?: string; unread: number }>;
  unread: number;
  broadcast: boolean;
  openNotifications: () => void;
  terminalHeader: ReactNode;
  focusDockPanel?: (id: string) => void;
  dockApi?: DockviewApi;
  headerPortalTarget?: HTMLElement | null;
  setHeaderPortalTarget?: (el: HTMLElement | null) => void;
}
const Ctx = createContext<DockCtx | null>(null);
export const useDock = () => useContext(Ctx)!;

/** Portals its children into the dockview tab header bar. */
export function SurfaceBarPortal({ children }: { children: ReactNode }) {
  const d = useDock();
  if (!d.headerPortalTarget) return <>{children}</>;
  return createPortal(children, d.headerPortalTarget);
}

function TerminalContent() {
  const d = useDock();
  const hostRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    let attempts = 0;
    const MAX = 30;
    const tryFind = () => {
      if (cancelled) return;
      const el = hostRef.current;
      if (!el) { if (++attempts < MAX) setTimeout(tryFind, 50); return; }
      const group = el.closest('.dv-groupview') as HTMLElement | null;
      if (!group) { if (++attempts < MAX) setTimeout(tryFind, 50); return; }
      const header = group.querySelector(':scope > .dv-tabs-and-actions-container') as HTMLElement | null;
      if (!header) { if (++attempts < MAX) setTimeout(tryFind, 50); return; }
      let slot = header.firstElementChild as HTMLElement | null;
      if (!slot || !slot.classList.contains('wimux-surface-slot')) {
        slot = document.createElement('div');
        slot.className = 'wimux-surface-slot';
        slot.style.cssText = 'display:flex;align-items:center;gap:2px;flex-shrink:1;overflow:hidden;';
        header.insertBefore(slot, header.firstChild);
      }
      d.setHeaderPortalTarget?.(slot);
    };
    tryFind();
    return () => { cancelled = true; d.setHeaderPortalTarget?.(null); };
  }, []);

  return (
    <div className="terminal-host" ref={hostRef}>
      {d.terminalHeader}
    <div className="surface-area">
      {d.surface ? (
        <SplitView
          wsId={d.workspace!.id}
          sId={d.surface.id}
          node={
            d.zoomedPaneId && d.surface.panes[d.zoomedPaneId]
              ? { id: "zoom", isLeaf: true, direction: "vertical", splitRatio: 0.5, paneId: d.zoomedPaneId }
              : d.surface.root
          }
          panes={d.surface.panes}
          focusedPaneId={d.surface.focusedPaneId}
          theme={d.theme}
          fontFamily={d.fontFamily}
          fontSize={d.fontSize}
          customColors={d.customColors}
          settings={d.settings}
          onFocus={d.focusPane}
          onClosePane={d.closePane}
          onTitle={d.setPaneTitle}
          onCwd={d.setPaneCwd}
          onNotify={d.onTerminalNotify}
          onSearchRequest={() => d.setSearchOpen(true)}
          onSplitRight={() => d.splitPane("vertical")}
          onSplitDown={() => d.splitPane("horizontal")}
          onZoom={d.toggleZoom}
          onCapture={(paneId) => api.capturePane(paneId)}
          onSetType={d.setPaneType}
          onRatio={d.setRatio}
        />
      ) : (
        <div className="empty-surface">
          <p>No surfaces. Create one to get started.</p>
          <button className="primary" onClick={d.newSurface}>New surface</button>
        </div>
      )}
    </div>
    <TerminalStatusBar />
    </div>
  );
}

function countLeaves(node?: SplitNode): number {
  if (!node) return 0;
  if (node.isLeaf) return node.paneId ? 1 : 0;
  return countLeaves(node.first) + countLeaves(node.second);
}

function TerminalStatusBar() {
  const d = useDock();
  const [fps, setFps] = useState(0);
  const [quotaStatus, setQuotaStatus] = useState("quota");

  useEffect(() => {
    let frame = 0;
    let raf = 0;
    let last = performance.now();
    const tick = (now: number) => {
      frame++;
      if (now - last >= 1000) {
        setFps(Math.round((frame * 1000) / (now - last)));
        frame = 0;
        last = now;
      }
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, []);

  useEffect(() => {
    let cancelled = false;
    const refresh = () => {
      api.getQuota()
        .then((snap) => {
          if (cancelled) return;
          const today = snap.windows.Today;
          setQuotaStatus(today && today.requests > 0 ? `quota ${today.requests}` : "quota");
        })
        .catch(() => {
          if (!cancelled) setQuotaStatus("quota");
        });
    };
    refresh();
    const timer = window.setInterval(refresh, 30000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, []);

  const paneCount = useMemo(() => countLeaves(d.surface?.root), [d.surface]);
  const paneLabel = d.zoomedPaneId && paneCount > 1
    ? `${paneCount} panes (1 zoomed)`
    : paneCount === 1 ? "1 pane" : `${paneCount} panes`;
  const statsLabel = `FPS ${fps || 0} · ${paneCount} active`;
  const leftLabel = d.workspace
    ? (d.wsStatus[d.workspace.id]?.branch ? `↗ ${d.wsStatus[d.workspace.id].branch}` : d.focusedCwd)
    : "";

  return (
    <div className="statusbar" role="status" aria-label="Terminal status">
      <div className="status-left mono">
        {leftLabel && <span className="status-branch" title={leftLabel}>{leftLabel}</span>}
      </div>
      <div className="status-right">
        <span className="status-item status-panes"><span className="status-icon">▣</span>{paneLabel}</span>
        <span className="status-item status-fps">{statsLabel}</span>
        {d.broadcast && <span className="status-broadcast">BROADCAST</span>}
        <span className="status-item status-local"><span className="status-dot" />Local</span>
        <span className="status-item"><span className="status-icon">▧</span>{quotaStatus}</span>
        <span className="status-hint">Ctrl+Shift+P: Commands</span>
      </div>
    </div>
  );
}

// Each dockable tool panel renders inside a docked container.
function panelBody(id: PanelId, d: DockCtx, close: () => void) {
  switch (id) {
    case "sidebar": return <WorkspacesContent />;
    case "agentChat": return <AgentChatPanel workspaceId={d.workspace?.id} surfaceId={d.surface?.id} paneId={d.focusedPaneId} onClose={close} />;
    case "logs": return <CommandLogsPanel workspace={d.workspace} onClose={close} onInsert={d.insertIntoFocused} onOpenVault={() => d.openPanel("vault")} />;
    case "vault": return <SessionVaultPanel workspace={d.workspace} onClose={close} />;
    case "quota": return <QuotaPanel onClose={close} />;
    case "agents": return <AgentsPanel onClose={close} />;
    case "wsSettings": return d.workspace ? <WorkspaceSettingsPanel workspace={d.workspace} onClose={close} /> : null;
  }
}

function PanelFrame({ id, children, onClose }: { id: PanelId; children: React.ReactNode; onClose: () => void }) {
  return (
    <div className="docked-panel">
      {children}
    </div>
  );
}

function ToolContent(props: IDockviewPanelProps) {
  const d = useDock();
  const id = props.api.id as PanelId;
  const close = () => d.closePanel(id);
  if (id === "sidebar") return <div className="docked-panel">{panelBody(id, d, close)}</div>;
  return <PanelFrame id={id} onClose={close}>{panelBody(id, d, close)}</PanelFrame>;
}

function WorkspacesContent() {
  const d = useDock();
  const dialog = useAppDialog();
  const [menu, setMenu] = useState<{ x: number; y: number; workspace: Workspace } | null>(null);
  const [menuFlipY, setMenuFlipY] = useState(false);
  const [menuFlipX, setMenuFlipX] = useState(false);
  const [filter, setFilter] = useState("");
  const [editingWorkspaceId, setEditingWorkspaceId] = useState<string | null>(null);
  const [editingWorkspaceName, setEditingWorkspaceName] = useState("");
  const menuRef = useRef<HTMLDivElement>(null);
  const setAccent = async (workspace: Workspace, accentColor: string) => {
    await api.updateWorkspace(workspace.id, { accentColor }).catch(() => {});
    d.refresh();
  };
  const startRename = (workspace: Workspace) => {
    setEditingWorkspaceId(workspace.id);
    setEditingWorkspaceName(workspace.name);
  };
  const commitRename = async (workspace: Workspace) => {
    const name = editingWorkspaceName.trim();
    setEditingWorkspaceId(null);
    if (!name || name === workspace.name) return;
    await api.updateWorkspace(workspace.id, { name }).catch(() => {});
    d.refresh();
  };
  const duplicate = async (workspace: Workspace) => {
    await api.createWorkspace(`${workspace.name} Copy`, workspace.workingDirectory).catch(() => {});
    d.refresh();
  };
  const setCustomAccent = async (workspace: Workspace) => {
    const color = await dialog.prompt("Workspace accent color", workspace.accentColor || "#818CF8", "Enter a CSS color value.");
    if (!color) return;
    await setAccent(workspace, color);
  };
  useEffect(() => {
    const req = d.workspaceRenameRequest;
    if (!req) return;
    const workspace = d.state.workspaces.find((w) => w.id === req.id);
    if (workspace) startRename(workspace);
  }, [d.workspaceRenameRequest?.token]);
  const colors = [
    ["Indigo", "#818CF8"],
    ["Green", "#10B981"],
    ["Amber", "#F59E0B"],
    ["Red", "#EF4444"],
    ["Cyan", "#06B6D4"],
    ["Purple", "#A855F7"],
    ["Slate", "#64748B"],
    ["Pink", "#EC4899"],
  ];
  useEffect(() => {
    if (!menu) return;
    const raf = window.requestAnimationFrame(() => {
      const el = menuRef.current;
      if (!el) return;
      const rect = el.getBoundingClientRect();
      const margin = 8;
      setMenuFlipY(rect.bottom > window.innerHeight - margin && rect.top > margin);
      setMenuFlipX(rect.right > window.innerWidth - margin && rect.left > margin);
    });
    const close = () => setMenu(null);
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") close(); };
    window.addEventListener("mousedown", close);
    window.addEventListener("keydown", onKey);
    return () => {
      window.cancelAnimationFrame(raf);
      window.removeEventListener("mousedown", close);
      window.removeEventListener("keydown", onKey);
    };
  }, [menu]);
  const visibleWorkspaces = d.state.workspaces.filter((w) => {
    const q = filter.trim().toLowerCase();
    if (!q) return true;
    const status = d.wsStatus[w.id];
    return `${w.name} ${w.workingDirectory ?? ""} ${status?.branch ?? ""} ${status?.workingDirectory ?? ""}`.toLowerCase().includes(q);
  });
  const paneInfoLines = (w: Workspace) => {
    const lines: string[] = [];
    const add = (value?: string) => {
      const v = (value ?? "").trim();
      if (!v) return;
      lines.push(v);
    };
    add(d.wsStatus[w.id]?.workingDirectory);
    add(w.workingDirectory);
    for (const s of w.surfaces) {
      for (const p of Object.values(s.panes)) {
        if (p.type === "web") add(`${p.url || "Browser"}`);
        else if (p.type === "notepad") add(`${p.title || "Notepad"}`);
        else add(`${p.workingDirectory || ""}`.trim());
      }
    }
    return lines;
  };
  return (
    <aside className="sidebar">
      <div className="ws-search"><input placeholder="Search workspaces..." value={filter} onChange={(e) => setFilter(e.target.value)} /></div>
      <div className="ws-list">
        {visibleWorkspaces.map((w) => (
          <div
            key={w.id}
            className={"ws-item" + (w.id === d.workspace?.id ? " active" : "")}
            onClick={() => d.selectWorkspace(w.id)}
            onContextMenu={(e) => {
              e.preventDefault();
              e.stopPropagation();
              setMenu({ x: e.clientX, y: e.clientY, workspace: w });
            }}
          >
            <span className="ws-active-bar" />
            <span className="ws-info">
              <span className="ws-title-row">
                <span className="ws-accent-dot" style={{ background: w.accentColor }} />
                {editingWorkspaceId === w.id ? (
                  <input
                    className="ws-name-input"
                    value={editingWorkspaceName}
                    autoFocus
                    onClick={(e) => e.stopPropagation()}
                    onChange={(e) => setEditingWorkspaceName(e.target.value)}
                    onBlur={() => { void commitRename(w); }}
                    onKeyDown={(e) => {
                      if (e.key === "Enter") { e.preventDefault(); void commitRename(w); }
                      if (e.key === "Escape") { e.preventDefault(); setEditingWorkspaceId(null); }
                    }}
                  />
                ) : (
                  <span className="ws-name" onDoubleClick={(e) => { e.stopPropagation(); startRename(w); }}>{w.name}</span>
                )}
              </span>
              {(d.wsStatus[w.id]?.branch) && <span className="ws-branch mono">{d.wsStatus[w.id]?.branch}</span>}
              {paneInfoLines(w).map((line) => <span key={line} className="ws-pane-line mono">{line}</span>)}
            </span>
            {(d.wsStatus[w.id]?.unread ?? 0) > 0 && <span className="badge">{d.wsStatus[w.id]?.unread}</span>}
            <button className="ws-close" onClick={(e) => { e.stopPropagation(); d.closeWorkspace(w.id); }} title="Close workspace"><XIcon /></button>
          </div>
        ))}
      </div>
      <div className="sidebar-foot">
        <button className="side-tool" onClick={d.newWorkspace}><PlusIcon /><span>New Workspace</span></button>
        <button className="side-tool" onClick={d.openNotifications}><BellIcon /><span>Notifications</span>{d.unread > 0 && <span className="side-badge">{d.unread}</span>}</button>
      </div>
      {menu && (
        <div
          ref={menuRef}
          className="app-context-menu"
          style={{
            left: menu.x,
            top: menu.y,
            transform: `${menuFlipX ? "translateX(-100%)" : "none"} ${menuFlipY ? " translateY(-100%)" : ""}`.trim(),
          }}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <button onClick={() => { const w = menu.workspace; setMenu(null); startRename(w); }}>Rename<span>F2</span></button>
          <button onClick={() => { const w = menu.workspace; setMenu(null); void duplicate(w); }}>Duplicate</button>
          <button onClick={() => { setMenu(null); d.selectWorkspace(menu.workspace.id); }}>Select Workspace</button>
          <button onClick={() => { setMenu(null); d.selectWorkspace(menu.workspace.id); d.newSurface(); }}>New Surface<span>Ctrl+T</span></button>
          <button onClick={() => { setMenu(null); d.openNotifications(); }}>Notifications</button>
          <div className="terminal-context-sep" />
          {colors.map(([label, color]) => (
            <button key={color} onClick={() => { const w = menu.workspace; setMenu(null); void setAccent(w, color); }}>
              <span className="context-color-dot" style={{ background: color }} />Accent: {label}
            </button>
          ))}
          <button onClick={() => { const w = menu.workspace; setMenu(null); void setCustomAccent(w); }}>Accent: Custom...</button>
          <button disabled>Move Up</button>
          <button disabled>Move Down</button>
          <div className="terminal-context-sep" />
          <button className="danger" onClick={() => { const w = menu.workspace; setMenu(null); d.closeWorkspace(w.id); }}>Close Workspace<span>Ctrl+Shift+W</span></button>
        </div>
      )}
    </aside>
  );
}

/** Each surface is its own dockview panel — renders SplitView directly. */
function SurfaceContent(props: IDockviewPanelProps) {
  const d = useDock();
  const sid = props.api.id;
  const ws = d.workspace;
  const s = ws?.surfaces.find((x) => x.id === sid);
  if (!ws) return <div className="empty-surface"><p>Loading workspace…</p></div>;
  if (!s) return <div className="empty-surface"><p>Loading surface…</p></div>;
  return (
    <div className="terminal-host">
      {d.terminalHeader}
      <div className="surface-area">
        <SplitView
          wsId={ws.id}
          sId={s.id}
          node={
            d.zoomedPaneId && s.panes[d.zoomedPaneId]
              ? { id: "zoom", isLeaf: true, direction: "vertical", splitRatio: 0.5, paneId: d.zoomedPaneId }
              : s.root
          }
          panes={s.panes}
          focusedPaneId={s.focusedPaneId}
          theme={d.theme}
          fontFamily={d.fontFamily}
          fontSize={d.fontSize}
          customColors={d.customColors}
          settings={d.settings}
          onFocus={d.focusPane}
          onClosePane={d.closePane}
          onTitle={d.setPaneTitle}
          onCwd={d.setPaneCwd}
          onNotify={d.onTerminalNotify}
          onSearchRequest={() => d.setSearchOpen(true)}
          onSplitRight={() => d.splitPane("vertical")}
          onSplitDown={() => d.splitPane("horizontal")}
          onZoom={d.toggleZoom}
          onCapture={(paneId) => api.capturePane(paneId)}
          onSetType={d.setPaneType}
          onRatio={d.setRatio}
        />
      </div>
      <TerminalStatusBar />
    </div>
  );
}

const COMPONENTS = {
  surface: (props: IDockviewPanelProps) => <SurfaceContent {...props} />,
  tool: (props: IDockviewPanelProps) => <ToolContent {...props} />,
};

const LockedTab = (props: IDockviewPanelHeaderProps) => <DockviewDefaultTab {...props} hideClose />;
const ToolTab = (props: IDockviewPanelHeaderProps) => <DockviewDefaultTab {...props} />;

/** Side tab with + button to add a new surface. Only shown for surface tab groups. */
function AddTabButton(props: IDockviewHeaderActionsProps) {
  const d = useDock();
  const surfaceIds = new Set(d.workspace?.surfaces.map((s) => s.id) ?? []);
  const hasSurfacePanel = props.panels.some((p) => surfaceIds.has(p.id));
  if (!hasSurfacePanel) return null;

  return (
    <button
      className="dv-add-tab-btn"
      onClick={(e) => { e.stopPropagation(); d.newSurface(); }}
      title="New surface (Ctrl+T)"
      style={{
        background: "transparent", border: "none", color: "var(--text-dim)",
        cursor: "pointer", padding: "0 10px", height: "100%",
        fontSize: 16, lineHeight: 1, display: "flex", alignItems: "center",
      }}
    ><PlusIcon /></button>
  );
}

const TAB_COMPONENTS = { locked: LockedTab, toolTab: ToolTab };
const RIGHT_TOOL_DEFAULT_WIDTH = 360;

function clampWidth(width: number | undefined, fallback: number, min: number, max: number) {
  if (!width || !Number.isFinite(width) || width <= 0) return fallback;
  return Math.min(max, Math.max(min, width));
}

interface Props extends DockCtx {
  openPanels: PanelId[];
  uiTheme: string;
}

function readSavedDockLayout(): any | null {
  try {
    const raw = window.localStorage.getItem(DOCK_LAYOUT_STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function writeSavedDockLayout(api: DockviewApi) {
  try {
    window.localStorage.setItem(DOCK_LAYOUT_STORAGE_KEY, JSON.stringify(api.toJSON()));
  } catch { }
}

function sanitizeSavedDockLayout(layout: any, validPanelIds: Set<string>): any | null {
  if (!layout || typeof layout !== "object") return null;
  const clone = structuredClone(layout);
  const panels = clone.panels;
  if (Array.isArray(panels)) {
    clone.panels = panels.filter((p: any) => p?.id && validPanelIds.has(p.id));
  } else if (panels && typeof panels === "object") {
    for (const id of Object.keys(panels)) {
      if (!validPanelIds.has(id)) delete panels[id];
    }
  }

  const prune = (node: any): any | null => {
    if (!node || typeof node !== "object") return null;
    if (Array.isArray(node.children)) {
      node.children = node.children.map(prune).filter(Boolean);
      return node.children.length > 0 ? node : null;
    }
    if (Array.isArray(node.panels)) {
      node.panels = node.panels.filter((id: string) => validPanelIds.has(id));
      return node.panels.length > 0 ? node : null;
    }
    if (typeof node.activePanel === "string" && !validPanelIds.has(node.activePanel))
      delete node.activePanel;
    return node;
  };

  if (clone.grid?.root) clone.grid.root = prune(clone.grid.root);
  return clone.grid?.root ? clone : null;
}

export function DockLayout({ openPanels, uiTheme, ...ctx }: Props) {
  const apiRef = useRef<DockviewApi | null>(null);
  const [dockApi, setDockApi] = useState<DockviewApi | null>(null);
  const [headerPortalTarget, setHeaderPortalTarget] = useState<HTMLElement | null>(null);
  const readyRef = useRef(false);
  const saveTimerRef = useRef<number | null>(null);
  const normalizeTimerRef = useRef<number | null>(null);
  const lastWidthsRef = useRef<Map<string, number>>(new Map());
  const absorbingRef = useRef(false);

  const isToolPanel = (id: string) => DOCK_PANELS.some((p) => p.id === id);
  const primarySurfacePanelId = (api: DockviewApi) => {
    const selected = ctx.workspace?.selectedSurfaceId;
    if (selected && api.getPanel(selected)) return selected;
    for (const s of ctx.workspace?.surfaces ?? []) {
      if (api.getPanel(s.id)) return s.id;
    }
    if (api.getPanel("surface-starter")) return "surface-starter";
    return api.panels.find((p) => !isToolPanel(p.id))?.id;
  };
  const rightToolPanelId = (api: DockviewApi, except?: PanelId) =>
    DOCK_PANELS.find((p) => p.id !== "sidebar" && p.id !== except && api.getPanel(p.id))?.id;
  const defaultToolWidth = (id: PanelId) => id === "sidebar" ? 260 : id === "agentChat" ? 320 : RIGHT_TOOL_DEFAULT_WIDTH;
  const normalizedToolWidth = (id: PanelId, width?: number) =>
    id === "sidebar"
      ? clampWidth(width, 260, 220, 360)
      : clampWidth(width, defaultToolWidth(id), 280, 520);

  function normalizeToolPanelWidths(api: DockviewApi) {
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        for (const { id } of DOCK_PANELS) {
          const p = api.getPanel(id);
          if (!p) continue;
          const width = normalizedToolWidth(id, p.api.width);
          lastWidthsRef.current.set(id, width);
          p.api.setSize({ width });
        }
      });
    });
  }

  function normalizePanelOrder(api: DockviewApi) {
    const surfaceId = primarySurfacePanelId(api);
    const surfacePanel = surfaceId ? api.getPanel(surfaceId) : undefined;
    if (!surfacePanel) {
      normalizeToolPanelWidths(api);
      return;
    }

    const surfaceRect = surfacePanel.api.group.element.getBoundingClientRect();
    const moves: Array<() => void> = [];
    const activeId = api.activePanel?.id;

    const sidebar = api.getPanel("sidebar");
    if (sidebar) {
      const rect = sidebar.api.group.element.getBoundingClientRect();
      if (rect.left > surfaceRect.left) {
        moves.push(() => sidebar.api.moveTo({ group: surfacePanel.api.group, position: "left", skipSetActive: true }));
      }
    }

    let rightTool = DOCK_PANELS
      .filter((p) => p.id !== "sidebar")
      .map((p) => api.getPanel(p.id))
      .find((panel) => {
        if (!panel) return false;
        const rect = panel.api.group.element.getBoundingClientRect();
        return rect.left > surfaceRect.left && panel.api.group !== surfacePanel.api.group;
      });

    for (const { id } of DOCK_PANELS) {
      if (id === "sidebar") continue;
      const panel = api.getPanel(id);
      if (!panel) continue;
      const rect = panel.api.group.element.getBoundingClientRect();
      const alreadyRight = rect.left > surfaceRect.left && panel.api.group !== surfacePanel.api.group;
      if (alreadyRight) {
        rightTool ??= panel;
        continue;
      }
      if (rightTool) {
        const targetGroup = rightTool.api.group;
        moves.push(() => panel.api.moveTo({ group: targetGroup, position: "center", skipSetActive: true }));
      } else {
        moves.push(() => panel.api.moveTo({ group: surfacePanel.api.group, position: "right", skipSetActive: true }));
        rightTool = panel;
      }
    }

    if (moves.length === 0) {
      normalizeToolPanelWidths(api);
      return;
    }

    absorbingRef.current = true;
    for (const move of moves) move();
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        normalizeToolPanelWidths(api);
        if (activeId) api.getPanel(activeId)?.api.setActive();
        absorbingRef.current = false;
        writeSavedDockLayout(api);
      });
    });
  }

  function scheduleNormalizePanelOrder(api: DockviewApi) {
    if (normalizeTimerRef.current !== null) window.cancelAnimationFrame(normalizeTimerRef.current);
    normalizeTimerRef.current = window.requestAnimationFrame(() => {
      normalizeTimerRef.current = null;
      normalizePanelOrder(api);
    });
  }

  function absorbWithTerminal(api: DockviewApi, mutate: () => void, extraWidths?: Map<string, number>) {
    const widths = new Map<string, number>();
    for (const p of api.panels) {
      if (isToolPanel(p.id)) widths.set(p.id, normalizedToolWidth(p.id as PanelId, p.api.width));
    }
    if (extraWidths) {
      for (const [id, w] of extraWidths) {
        if (w > 0 && isToolPanel(id)) widths.set(id, normalizedToolWidth(id as PanelId, w));
      }
    }
    absorbingRef.current = true;
    mutate();
    requestAnimationFrame(() => {
      requestAnimationFrame(() => {
        for (const [id, width] of widths) {
          const p = api.getPanel(id);
          if (p && width > 0) p.api.setSize({ width });
        }
        absorbingRef.current = false;
      });
    });
  }

  const saveSoon = (api: DockviewApi) => {
    if (saveTimerRef.current !== null) window.clearTimeout(saveTimerRef.current);
    saveTimerRef.current = window.setTimeout(() => {
      saveTimerRef.current = null;
      writeSavedDockLayout(api);
    }, 150);
  };

  const onReady = (e: DockviewReadyEvent) => {
    apiRef.current = e.api;
    setDockApi(e.api);
    const savedLayout = readSavedDockLayout();
    if (savedLayout) {
      try {
        const validPanelIds = new Set<string>([
          ...DOCK_PANELS.map((p) => p.id),
          ...(ctx.workspace?.surfaces.map((s) => s.id) ?? []),
        ]);
        if ((ctx.workspace?.surfaces.length ?? 0) === 0) validPanelIds.add("surface-starter");
        const sanitized = sanitizeSavedDockLayout(savedLayout, validPanelIds);
        if (sanitized) e.api.fromJSON(sanitized);
        normalizeToolPanelWidths(e.api);
        scheduleNormalizePanelOrder(e.api);
        writeSavedDockLayout(e.api);
      } catch {
        e.api.clear();
      }
    }
    if (e.api.totalPanels === 0) {
      // Add a starter surface panel immediately so there's a terminal tab on first load.
      const firstSurfaceId = "surface-starter";
      e.api.addPanel({
        id: firstSurfaceId,
        component: "surface",
        tabComponent: "toolTab",
        title: "Terminal 1",
        position: { direction: "right" },
        initialWidth: 500,
      });
      e.api.addPanel({
        id: "sidebar",
        component: "tool",
        tabComponent: "toolTab",
        title: "Workspaces",
        position: { direction: "left" },
        initialWidth: 260,
        inactive: true,
      });
      e.api.getPanel(firstSurfaceId)?.api.setActive();
      scheduleNormalizePanelOrder(e.api);
      writeSavedDockLayout(e.api);
    }
    e.api.onDidRemovePanel((p) => {
      if (absorbingRef.current) return;
      const savedWidths = new Map(lastWidthsRef.current);
      if (isToolPanel(p.id)) {
        ctx.closePanel(p.id as PanelId);
      }
      if (ctx.workspace?.surfaces.some((s) => s.id === p.id)) {
        ctx.closeSurface(p.id);
      }
      if (!absorbingRef.current) {
        requestAnimationFrame(() => {
          requestAnimationFrame(() => {
            for (const [id, width] of savedWidths) {
              if (id !== p.id) {
                const panel = e.api.getPanel(id);
                if (panel && width > 0 && isToolPanel(id)) panel.api.setSize({ width });
              }
            }
          });
        });
      }
    });
    e.api.onDidAddPanel(() => {});
    e.api.onDidLayoutChange(() => {
      saveSoon(e.api);
      for (const p of e.api.panels) {
        if (isToolPanel(p.id)) lastWidthsRef.current.set(p.id, normalizedToolWidth(p.id as PanelId, p.api.width));
      }
    });

    readyRef.current = true;
  };

  // Sync workspace surfaces to dockview panels — each surface gets a native tab.
  useEffect(() => {
    const api = apiRef.current;
    const ws = ctx.workspace;
    if (!api || !readyRef.current || !ws) return;
    const surfaceIds = new Set(ws.surfaces.map((s) => s.id));
    absorbWithTerminal(api, () => {
      if (surfaceIds.size > 0) {
        const starter = api.getPanel("surface-starter");
        if (starter) {
          try { starter.api.close(); } catch { /* ignore stale starter cleanup */ }
        }
      }
      // Create panels for surfaces that don't have one yet.
      for (const s of ws.surfaces) {
        if (!api.getPanel(s.id)) {
          // Find a sibling surface panel to anchor next to (so the new tab joins
          // the same group instead of spawning a separate dock area).
          let position: any = { direction: "right" as const };
          for (const existingId of surfaceIds) {
            if (existingId !== s.id) {
              const anchor = api.getPanel(existingId);
              if (anchor) {
                position = { referencePanel: existingId, direction: "within" as const };
                break;
              }
            }
          }
          const title = s.name || `Surface ${surfaceIds.size + 1}`;
          api.addPanel({
            id: s.id,
            component: "surface",
            tabComponent: "toolTab",
            title,
            position,
          });
        } else {
          const p = api.getPanel(s.id);
          if (p && p.title !== s.name) p.api.setTitle(s.name);
        }
      }
      // Close panels for surfaces that no longer exist.
      for (const p of api.panels) {
        if (p.id === "sidebar") continue;
        if (DOCK_PANELS.some((dp) => dp.id === p.id)) continue;
        if (!surfaceIds.has(p.id)) {
          try { p.api.close(); } catch { /* ignore */ }
        }
      }
    });
  }, [ctx.workspace?.surfaces.length, ctx.workspace?.surfaces.map((s) => s.id + ":" + s.name).join(",")]);

  // Reconcile tool panels.
  useEffect(() => {
    const api = apiRef.current;
    if (!api || !readyRef.current) return;
    for (const { id, label } of DOCK_PANELS) {
      const exists = api.getPanel(id);
      if (openPanels.includes(id) && !exists) {
        const savedW = normalizedToolWidth(id, lastWidthsRef.current.get(id));
        const extraWidths = new Map<string, number>();
        extraWidths.set(id, savedW);
        const surfaceAnchor = primarySurfacePanelId(api);
        const rightAnchor = rightToolPanelId(api, id);
        const position = id === "sidebar"
          ? surfaceAnchor
            ? { referencePanel: surfaceAnchor, direction: "left" as const }
            : { direction: "left" as const }
          : rightAnchor
            ? { referencePanel: rightAnchor, direction: "within" as const }
            : surfaceAnchor
              ? { referencePanel: surfaceAnchor, direction: "right" as const }
              : { direction: "right" as const };
        absorbWithTerminal(api, () =>
          api.addPanel({ id, component: "tool", tabComponent: "toolTab", title: label, position, initialWidth: savedW }), extraWidths);
      } else if (!openPanels.includes(id) && exists) {
        const w = exists.api.width;
        if (w > 0) lastWidthsRef.current.set(id, normalizedToolWidth(id, w));
        absorbWithTerminal(api, () => exists.api.close());
      }
    }
    const want = openPanels[openPanels.length - 1];
    if (want) api.getPanel(want)?.api.setActive();
    scheduleNormalizePanelOrder(api);
  }, [openPanels]);

  const focusDockPanel = useCallback((id: string) => {
    apiRef.current?.getPanel(id)?.api.setActive();
  }, []);

  const newSurface = ctx.newSurface;
  const workspace = ctx.workspace;
  const newSurfaceRef = useRef(newSurface);
  useEffect(() => { newSurfaceRef.current = newSurface; }, [newSurface]);

  return (
    <Ctx.Provider value={{ ...ctx, focusDockPanel, dockApi: dockApi ?? undefined, headerPortalTarget, setHeaderPortalTarget }}>
      <div className="wimux-layout">
        <DockviewReact
          className={`dv-dock ${uiTheme}`}
          components={COMPONENTS}
          tabComponents={TAB_COMPONENTS}
          rightHeaderActionsComponent={AddTabButton}
          onReady={onReady}
        />
      </div>
    </Ctx.Provider>
  );
}
