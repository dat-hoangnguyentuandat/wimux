import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { DockLayout, DOCK_LAYOUT_STORAGE_KEY, DOCK_PANELS, type PanelId } from "./components/DockLayout";
import { AgentSettingsPanel } from "./components/AgentSettingsPanel";
import { CommandPalette } from "./components/CommandPalette";
import { HistoryPicker } from "./components/HistoryPicker";
import { NotificationsPanel } from "./components/NotificationsPanel";
import { SettingsModal } from "./components/SettingsModal";
import { SnippetsPanel } from "./components/SnippetsPanel";
import { TemplatesPanel } from "./components/TemplatesPanel";
import { QuickOpen } from "./components/QuickOpen";
import { AppDialogProvider, useAppDialog } from "./components/AppDialog";
import { api } from "./lib/api";
import { terminalBus } from "./lib/terminalBus";
import type { AppState, Surface, TerminalTheme, Workspace } from "./lib/api";
import "./styles.css";

type Overlay = "settings" | "notifications" | "snippets" | "history" | "templates" | "palette" | "quickOpen" | null;

function readSavedDockPanelIds(): string[] | null {
  try {
    const raw = window.localStorage.getItem(DOCK_LAYOUT_STORAGE_KEY);
    if (!raw) return null;
    const layout = JSON.parse(raw);
    const panels = layout?.panels;
    if (Array.isArray(panels)) {
      return panels.map((p: any) => p.id).filter(Boolean);
    }
    if (panels && typeof panels === "object") {
      return Object.keys(panels);
    }
    const gridPanels = layout?.grid?.root?.children?.flatMap((c: any) => c?.panels ?? []) ?? [];
    return gridPanels.map((p: any) => p.id).filter(Boolean);
  } catch { return null; }
}

function initialActivePanels(): PanelId[] {
  const savedPanels = readSavedDockPanelIds();
  if (!savedPanels) return ["sidebar"];
  const known = new Set(DOCK_PANELS.map((p) => p.id));
  return savedPanels.filter((id): id is PanelId => known.has(id as PanelId));
}

export default function App() {
  return (
    <AppDialogProvider>
      <AppContent />
    </AppDialogProvider>
  );
}

function AppContent() {
  const dialog = useAppDialog();
  const [state, setState] = useState<AppState | null>(null);
  const [themes, setThemes] = useState<TerminalTheme[]>([]);
  const [settings, setSettings] = useState<any>(null);
  const [overlay, setOverlay] = useState<Overlay>(null);
  const [activePanels, setActivePanels] = useState<PanelId[]>(initialActivePanels);
  const sidebarOpen = activePanels.includes("sidebar");
  const toggleSidebar = useCallback(() => {
    setActivePanels((p) => p.includes("sidebar") ? p.filter((x) => x !== "sidebar") : [...p, "sidebar"]);
  }, []);
  const [unread, setUnread] = useState(0);
  const [searchOpen, setSearchOpen] = useState(false);
  const [searchTerm, setSearchTerm] = useState("");
  const searchInputRef = useRef<HTMLInputElement>(null);
  const [zoomedPaneId, setZoomedPaneId] = useState<string | null>(null);
  const [broadcast, setBroadcast] = useState(false);
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const [surfaceMenu, setSurfaceMenu] = useState<{ x: number; y: number; surface: Surface } | null>(null);
  const [shells, setShells] = useState<{ name: string; path: string }[]>([]);
  const [shellMenuOpen, setShellMenuOpen] = useState(false);
  const [wsStatus, setWsStatus] = useState<Record<string, { branch?: string; workingDirectory?: string; unread: number }>>({});
  const [workspaceRenameRequest, setWorkspaceRenameRequest] = useState<{ id: string; token: number } | null>(null);

  const applySettings = useCallback((s: any) => {
    setSettings(s);
    document.documentElement.setAttribute("data-ui-theme", s?.uiThemeName ?? "Dark+");
  }, []);

  const refresh = useCallback(async () => setState(await api.getState()), []);
  const refreshUnread = useCallback(() => {
    api.getNotifications().then((r) => setUnread(r.unread)).catch(() => {});
    api.getWorkspaceStatus().then((rows) => {
      const map: Record<string, any> = {};
      for (const r of rows) map[r.id] = { branch: r.branch, workingDirectory: r.workingDirectory, unread: r.unread };
      setWsStatus(map);
    }).catch(() => {});
  }, []);

  const onTerminalNotify = useCallback(() => {
    api.getNotifications().then((r) => {
      setUnread(r.unread);
      const latest = r.items.find((n) => !n.isRead);
    }).catch(() => {});
  }, []);

  useEffect(() => {
    refresh().then(() => {
      api.getThemes().then(setThemes).catch(() => {});
      api.getSettings().then(applySettings).catch(() => {});
    });
    api.getShells().then(setShells).catch(() => setShells([]));
    refreshUnread();
    if ("Notification" in window && Notification.permission === "default")
      Notification.requestPermission().catch(() => {});
    const t = setInterval(refreshUnread, 5000);
    return () => clearInterval(t);
  }, [refresh, refreshUnread, applySettings]);

  useEffect(() => {
    const id = crypto.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    api.openBrowserSession(id).catch(() => {});
    const ping = window.setInterval(() => {
      api.pingBrowserSession(id).catch(() => {});
    }, 8000);
    const close = () => {
      void api.closeBrowserSession(id);
    };
    window.addEventListener("pagehide", close);
    window.addEventListener("beforeunload", close);
    return () => {
      window.clearInterval(ping);
      close();
      window.removeEventListener("pagehide", close);
      window.removeEventListener("beforeunload", close);
    };
  }, []);

  useEffect(() => {
    const suppressBrowserContextMenu = (e: MouseEvent) => {
      e.preventDefault();
    };
    window.addEventListener("contextmenu", suppressBrowserContextMenu, { capture: true });
    return () => window.removeEventListener("contextmenu", suppressBrowserContextMenu, { capture: true } as any);
  }, []);

  const workspace = useMemo<Workspace | undefined>(
    () => state?.workspaces.find((w) => w.id === state.selectedWorkspaceId) ?? state?.workspaces[0], [state]);
  const surface = useMemo<Surface | undefined>(
    () => workspace?.surfaces.find((s) => s.id === workspace.selectedSurfaceId) ?? workspace?.surfaces[0], [workspace]);

  const activeTheme = useMemo(() => themes.find((t) => t.name === settings?.themeName), [themes, settings?.themeName]);
  const fontFamily = settings?.fontFamily ?? "Cascadia Code";
  const fontSize = settings?.fontSize ?? 14;
  const customColors = useMemo(() => settings?.useCustomTerminalColors ? {
    background: settings?.customTerminalBackground, foreground: settings?.customTerminalForeground,
    cursor: settings?.customTerminalCursor, selection: settings?.customTerminalSelection,
  } : undefined, [settings]);
  const focusedPaneId = surface?.focusedPaneId ?? Object.keys(surface?.panes ?? {})[0];
  const paneCount = useMemo(() => {
    const countLeaves = (node: any): number => {
      if (!node) return 0;
      if (node.isLeaf) return node.paneId ? 1 : 0;
      return countLeaves(node.first) + countLeaves(node.second);
    };
    return countLeaves(surface?.root);
  }, [surface?.root]);
  const canArrangePanes = paneCount > 1;
  const focusedCwd = useMemo(() => {
    if (!focusedPaneId || !surface) return workspace?.workingDirectory ?? "";
    return surface.panes[focusedPaneId]?.workingDirectory || workspace?.workingDirectory || "";
  }, [focusedPaneId, surface, workspace]);

  const selectWorkspace = useCallback(async (id: string) => { await api.selectWorkspace(id); await refresh(); }, [refresh]);
  const selectSurface = useCallback(async (sId: string) => {
    if (!workspace) return; await api.selectSurface(workspace.id, sId); await refresh();
  }, [workspace, refresh]);
  const closeWorkspace = useCallback(async (id: string) => { await api.deleteWorkspace(id); await refresh(); }, [refresh]);
  const closeSurface = useCallback(async (sId: string) => {
    if (!workspace) return; await api.deleteSurface(workspace.id, sId); await refresh();
  }, [workspace, refresh]);
  const newWorkspace = useCallback(async () => { await api.createWorkspace(); await refresh(); }, [refresh]);
  const newSurface = useCallback(async () => {
    if (!workspace) return; await api.createSurface(workspace.id); await refresh();
  }, [workspace, refresh]);

  // Auto-create first surface if workspace exists but has none.
  const autoCreatedRef = useRef(false);
  useEffect(() => {
    if (workspace && workspace.surfaces.length === 0 && !autoCreatedRef.current) {
      autoCreatedRef.current = true;
      newSurface();
    }
  }, [workspace?.id, workspace?.surfaces.length, newSurface]);

  const openWithShell = useCallback(async (shellPath?: string) => {
    if (!workspace) return;
    if (workspace.surfaces.length === 0) {
      await api.createSurface(workspace.id, undefined, shellPath, "terminal");
      await refresh();
    } else {
      const sid = surface?.id ?? workspace.surfaces[0].id;
      const paneId = focusedPaneId && surface?.panes[focusedPaneId]
        ? focusedPaneId
        : Object.keys(surface?.panes ?? {})[0];
      if (!paneId) return;
      await api.split(workspace.id, sid, paneId, "vertical", shellPath, "terminal");
      await refresh();
    }
  }, [workspace, surface, focusedPaneId, refresh]);

  const openBrowserPane = useCallback(async () => {
    if (!workspace) return;
    if (workspace.surfaces.length === 0) {
      await api.createSurface(workspace.id, undefined, undefined, "web", "https://www.google.com/webhp?igu=1");
      await refresh();
    } else {
      const sid = surface?.id ?? workspace.surfaces[0].id;
      const paneId = focusedPaneId && surface?.panes[focusedPaneId]
        ? focusedPaneId
        : Object.keys(surface?.panes ?? {})[0];
      if (!paneId) return;
      await api.split(workspace.id, sid, paneId, "vertical", undefined, "web", "https://www.google.com/webhp?igu=1");
      await refresh();
    }
  }, [workspace, surface, focusedPaneId, refresh]);

  const renameWorkspace = useCallback(async () => {
    if (!workspace) return;
    setActivePanels((p) => (p.includes("sidebar") ? p : [...p, "sidebar"]));
    setWorkspaceRenameRequest({ id: workspace.id, token: Date.now() });
  }, [workspace]);

  const renameSurface = useCallback(async (s: Surface) => {
    const name = await dialog.prompt("Rename surface", s.name);
    if (!name || !workspace) return;
    await api.renameSurface(workspace.id, s.id, name).catch(() => {});
    refresh();
  }, [workspace, refresh, dialog]);

  const duplicateSurface = useCallback(async (s: Surface) => {
    if (!workspace) return;
    const ns = await api.createSurface(workspace.id);
    const state2 = await api.getState();
    const newSurface = state2?.workspaces.find((w) => w.id === workspace.id)?.surfaces.find((x) => x.id === ns.id);
    if (!newSurface) return;
    for (const pane of Object.values(s.panes)) {
      await api.split(workspace.id, ns.id, Object.keys(s.panes)[0] ?? ns.id, "vertical",
        (pane as any).shellPath, pane.type as any, (pane as any).url);
    }
    await refresh();
  }, [workspace, refresh]);

  const closeOtherSurfaces = useCallback(async (sId: string) => {
    if (!workspace) return;
    for (const s of workspace.surfaces) {
      if (s.id !== sId) await api.deleteSurface(workspace.id, s.id);
    }
    await refresh();
  }, [workspace, refresh]);

  const focusPane = useCallback(async (paneId: string) => {
    setState((prev) => {
      if (!prev) return prev;
      const next = structuredClone(prev) as AppState;
      for (const w of next.workspaces) for (const s of w.surfaces) {
        if (s.panes[paneId]) { s.focusedPaneId = paneId; w.selectedSurfaceId = s.id; return next; }
      }
      return prev;
    });
    if (workspace && surface) {
      api.focusPane(workspace.id, surface.id, paneId).catch(() => {});
    }
  }, [workspace, surface]);

  const closePane = useCallback(async (paneId: string) => {
    if (!workspace) return;
    for (const s of workspace.surfaces) {
      if (s.panes[paneId]) { await api.closePane(workspace.id, s.id, paneId); break; }
    }
    await refresh();
  }, [workspace, refresh]);

  const setPaneTitle = useCallback((paneId: string, title: string) => {
    setState((prev) => {
      if (!prev) return prev;
      const next = structuredClone(prev) as AppState;
      for (const w of next.workspaces) for (const s of w.surfaces)
        if (s.panes[paneId]) { s.panes[paneId].title = title; return next; }
      return prev;
    });
  }, []);
  const setPaneCwd = useCallback((paneId: string, cwd: string) => {
    setState((prev) => {
      if (!prev) return prev;
      const next = structuredClone(prev) as AppState;
      for (const w of next.workspaces) for (const s of w.surfaces)
        if (s.panes[paneId]) { s.panes[paneId].workingDirectory = cwd; return next; }
      return prev;
    });
  }, []);
  const setPaneType = useCallback((paneId: string, type: string) => {
    setState((prev) => {
      if (!prev) return prev;
      const next = structuredClone(prev) as AppState;
      for (const w of next.workspaces) for (const s of w.surfaces)
        if (s.panes[paneId]) { s.panes[paneId].type = type as any; return next; }
      return prev;
    });
  }, []);
  const setRatio = useCallback((nodeId: string, ratio: number) => {
    if (!workspace || !surface) return;
    api.setRatio(workspace.id, surface.id, nodeId, ratio).catch(() => {});
    setState((prev) => {
      if (!prev) return prev;
      const next = structuredClone(prev) as AppState;
      for (const w of next.workspaces) for (const s of w.surfaces) {
        if (s.id === surface?.id) {
          const updateNode = (n: any): any => {
            if (!n) return n;
            if (n.id === nodeId) return { ...n, splitRatio: ratio };
            if (!n.isLeaf) return { ...n, first: updateNode(n.first), second: updateNode(n.second) };
            return n;
          };
          s.root = updateNode(s.root);
          return next;
        }
      }
      return prev;
    });
  }, [workspace, surface]);
  const toggleZoom = useCallback(() => {
    if (!focusedPaneId) return;
    setZoomedPaneId((z) => (z === focusedPaneId ? null : focusedPaneId));
  }, [focusedPaneId]);
  const equalizeSurface = useCallback(async () => {
    if (!workspace || !surface) return;
    const nodes: string[] = [];
    const walk = (node: any) => { if (!node || node.isLeaf) return; nodes.push(node.id); walk(node.first); walk(node.second); };
    walk(surface.root);
    await Promise.all(nodes.map((nodeId) => api.setRatio(workspace.id, surface.id, nodeId, 0.5).catch(() => {})));
    await refresh();
  }, [workspace, surface, refresh]);
  const applyLayout = useCallback(async (cols: number, rows: number) => {
    if (!workspace || !surface) return;
    let current = surface;
    const leaves = (root: any) => {
      const ids: string[] = [];
      const walk = (node: any) => {
        if (!node) return;
        if (node.isLeaf) { if (node.paneId) ids.push(node.paneId); return; }
        walk(node.first); walk(node.second);
      };
      walk(root);
      return ids;
    };
    const paneIds = leaves(current.root);
    const keepPaneId = focusedPaneId && paneIds.includes(focusedPaneId) ? focusedPaneId : paneIds[0];
    if (!keepPaneId) return;
    setZoomedPaneId(null);
    await api.focusPane(workspace.id, current.id, keepPaneId).catch(() => {});
    for (const paneId of paneIds) {
      if (paneId !== keepPaneId) current = await api.closePane(workspace.id, current.id, paneId);
    }
    let columnPaneIds = [keepPaneId];
    for (let c = 1; c < cols; c++) {
      current = await api.split(workspace.id, current.id, columnPaneIds[columnPaneIds.length - 1], "vertical");
      columnPaneIds = leaves(current.root);
    }
    if (rows > 1) {
      for (const paneId of [...columnPaneIds]) {
        await api.focusPane(workspace.id, current.id, paneId).catch(() => {});
        let targetPaneId = paneId;
        for (let r = 1; r < rows; r++) {
          current = await api.split(workspace.id, current.id, targetPaneId, "horizontal");
          targetPaneId = current.focusedPaneId ?? targetPaneId;
        }
      }
    }
    const nodes: string[] = [];
    const collectNodes = (node: any) => { if (!node || node.isLeaf) return; nodes.push(node.id); collectNodes(node.first); collectNodes(node.second); };
    collectNodes(current.root);
    await Promise.all(nodes.map((nodeId) => api.setRatio(workspace.id, current.id, nodeId, 0.5).catch(() => {})));
    await refresh();
  }, [workspace, surface, focusedPaneId, refresh]);
  const applyMainStackLayout = useCallback(async () => {
    if (!workspace || !surface) return;
    await applyLayout(2, 1);
    const stateAfterColumns = await api.getState();
    const ws = stateAfterColumns.workspaces.find((w) => w.id === workspace.id);
    const current = ws?.surfaces.find((s) => s.id === surface.id);
    if (!current) return;
    const ids: string[] = [];
    const walk = (node: any) => { if (!node) return; if (node.isLeaf) { if (node.paneId) ids.push(node.paneId); return; } walk(node.first); walk(node.second); };
    walk(current.root);
    const rightPaneId = ids[1];
    if (rightPaneId) {
      await api.focusPane(workspace.id, current.id, rightPaneId).catch(() => {});
      const updated = await api.split(workspace.id, current.id, rightPaneId, "horizontal").catch(() => null);
      const nodes: string[] = [];
      const collectNodes = (node: any) => { if (!node || node.isLeaf) return; nodes.push(node.id); collectNodes(node.first); collectNodes(node.second); };
      collectNodes(updated?.root);
      await Promise.all(nodes.map((nodeId) => api.setRatio(workspace.id, current.id, nodeId, 0.5).catch(() => {})));
      await refresh();
    }
  }, [workspace, surface, applyLayout, refresh]);
  const insertIntoFocused = useCallback((text: string) => {
    terminalBus.write(focusedPaneId, text);
  }, [focusedPaneId]);
  const openPanel = useCallback((id: PanelId) => { setActivePanels((p) => (p.includes(id) ? p : [...p, id])); }, []);
  const closePanel = useCallback((id: PanelId) => { setActivePanels((p) => p.filter((x) => x !== id)); }, []);
  const selectWorkspaceByIndex = useCallback(async (n: number) => {
    const ws = state?.workspaces[n]; if (ws) await selectWorkspace(ws.id);
  }, [state, selectWorkspace]);
  const cycleSurface = useCallback(async (dir: number) => {
    if (!workspace) return; const idx = workspace.surfaces.findIndex((s) => s.id === surface?.id);
    const next = (idx + dir + workspace.surfaces.length) % workspace.surfaces.length;
    await selectSurface(workspace.surfaces[next].id);
  }, [workspace, surface, selectSurface]);
  const focusAdjacent = useCallback(async (dir: number) => {
    if (!surface) return;
    const ids: string[] = []; const walk = (n: any) => { if (!n) return; if (n.isLeaf) { if (n.paneId) ids.push(n.paneId); return; } walk(n.first); walk(n.second); };
    walk(surface.root); const idx = ids.indexOf(focusedPaneId ?? "");
    if (idx >= 0 && ids.length > 1) { const next = (idx + dir + ids.length) % ids.length; await focusPane(ids[next]); }
  }, [surface, focusedPaneId, focusPane]);

  const splitPane = useCallback((dir: "vertical" | "horizontal") => {
    if (!workspace || !surface || !focusedPaneId) return;
    api.split(workspace.id, surface.id, focusedPaneId, dir).then(() => refresh()).catch(() => {});
  }, [workspace, surface, focusedPaneId, refresh]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const ctrl = e.ctrlKey || e.metaKey; if (!ctrl) return;
      const k = e.key.toLowerCase(); const shift = e.shiftKey; const alt = e.altKey;
      if (shift && k === "p") { e.preventDefault(); setOverlay("palette"); }
      else if (!shift && !alt && k === "p") { e.preventDefault(); setOverlay("quickOpen"); }
      else if (!shift && e.key === ",") { e.preventDefault(); setOverlay("settings"); }
      else if (!shift && !alt && k === "b") { e.preventDefault(); toggleSidebar(); }
      else if (!shift && !alt && k === "n") { e.preventDefault(); newWorkspace(); }
      else if (!shift && !alt && k === "f2") { e.preventDefault(); renameWorkspace(); }
      else if (shift && k === "w") { e.preventDefault(); if (workspace) closeWorkspace(workspace.id); }
      else if (!shift && !alt && k === "t") { e.preventDefault(); newSurface(); }
      else if (!shift && !alt && k === "w") { e.preventDefault(); if (surface) closeSurface(surface.id); }
      else if (shift && k === "d") { e.preventDefault(); splitPane("horizontal"); }
      else if (!shift && !alt && k === "d") { e.preventDefault(); splitPane("vertical"); }
      else if (alt && !shift && k === "h") { e.preventDefault(); setOverlay("history"); }
      else if (shift && !alt && k === "j") { e.preventDefault(); openPanel("agentChat"); }
      else if (shift && !alt && k === "l") { e.preventDefault(); openPanel("logs"); }
      else if (shift && !alt && k === "v") { e.preventDefault(); openPanel("vault"); }
      else if (shift && !alt && k === "q") { e.preventDefault(); openPanel("quota"); }
      else if (shift && !alt && k === "a") { e.preventDefault(); openPanel("agents"); }
      else if (shift && !alt && k === "s") { e.preventDefault(); setOverlay("snippets"); }
      else if (shift && k === "z") { e.preventDefault(); toggleZoom(); }
      else if (alt && !shift && k === "b") { e.preventDefault(); setBroadcast((v) => !v); }
      else if (shift && k === "f") { e.preventDefault(); setSearchOpen(true); }
      else if (!shift && !alt && k === "tab") { e.preventDefault(); cycleSurface(1); }
      else if (shift && k === "tab") { e.preventDefault(); cycleSurface(-1); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [newWorkspace, newSurface, splitPane, renameWorkspace, closeWorkspace, closeSurface, cycleSurface, selectWorkspaceByIndex, workspace, surface, state, toggleZoom, focusAdjacent, setBroadcast, refreshUnread]);

  const commands = useMemo(() => [
    { id: "workspace.new", title: "Workspace: New", hint: "Ctrl+N", run: newWorkspace },
    { id: "workspace.rename", title: "Workspace: Rename", hint: "F2", run: renameWorkspace },
    { id: "surface.new", title: "Surface: New", hint: "Ctrl+T", run: newSurface },
    { id: "surface.splitRight", title: "Surface: Split Right", hint: "Ctrl+D", run: () => splitPane("vertical") },
    { id: "surface.splitDown", title: "Surface: Split Down", hint: "Ctrl+Shift+D", run: () => splitPane("horizontal") },
    { id: "surface.zoom", title: "Surface: Toggle Zoom", hint: "Ctrl+Shift+Z", run: toggleZoom },
    { id: "surface.resetSplits", title: "Surface: Reset Splits", run: async () => {
      if (!workspace || !surface) return;
      const nodes: string[] = [];
      const walk = (node: any) => { if (!node || node.isLeaf) return; nodes.push(node.id); walk(node.first); walk(node.second); };
      walk(surface.root);
      await Promise.all(nodes.map((nodeId) => api.setRatio(workspace.id, surface.id, nodeId, 0.5).catch(() => {})));
      await refresh();
    }},
    { id: "palette", title: "Search", hint: "Ctrl+Shift+F", run: () => setSearchOpen(true) },
    { id: "palette.cmd", title: "Command Palette", hint: "Ctrl+Shift+P", run: () => setOverlay("palette") },
    { id: "palette.snippets", title: "Snippets", hint: "Ctrl+Shift+S", run: () => setOverlay("snippets") },
  ], [newWorkspace, renameWorkspace, newSurface, splitPane, focusedPaneId, toggleZoom, workspace, surface, refresh]);

  const menus: Record<string, { label: string; hint?: string; run: () => void }[]> = useMemo(() => ({
    File: [
      { label: "New Workspace", hint: "Ctrl+N", run: newWorkspace },
      { label: "New Surface", hint: "Ctrl+T", run: newSurface },
      { label: "Settings", hint: "Ctrl+,", run: () => setOverlay("settings") },
      { label: "Exit", run: () => window.close() },
    ],
    Window: [
      { label: "Split Right", hint: "Ctrl+D", run: () => splitPane("vertical") },
      { label: "Split Down", hint: "Ctrl+Shift+D", run: () => splitPane("horizontal") },
      { label: "Toggle Zoom", hint: "Ctrl+Shift+Z", run: toggleZoom },
      { label: "Reset Splits", run: () => {
        if (!workspace || !surface) return;
        const nodes: string[] = [];
        const walk = (node: any) => { if (!node || node.isLeaf) return; nodes.push(node.id); walk(node.first); walk(node.second); };
        walk(surface.root);
        Promise.all(nodes.map((nodeId) => api.setRatio(workspace.id, surface.id, nodeId, 0.5).catch(() => {}))).then(() => refresh());
      }},
    ],
    View: [
      { label: "Command Logs", hint: "Ctrl+Shift+L", run: () => openPanel("logs") },
      { label: "Session Vault", hint: "Ctrl+Shift+V", run: () => openPanel("vault") },
      { label: "Quota Tracking", hint: "Ctrl+Shift+Q", run: () => openPanel("quota") },
      { label: "AI Agents", hint: "Ctrl+Shift+A", run: () => openPanel("agents") },
      { label: "Terminal", run: () => { if (workspace?.surfaces.length === 0) newSurface(); } },
      { label: "Toggle Workspaces", hint: "Ctrl+B", run: () => toggleSidebar() },
      { label: "Agent Chat", hint: "Ctrl+Shift+J", run: () => openPanel("agentChat") },
    ],
    Help: [
      { label: "Keyboard Shortcuts", run: () => setOverlay("palette") },
      { label: "About", run: () => { void dialog.alert("wimux", "A terminal multiplexer for AI coding workflows."); } },
    ],
  }), [newWorkspace, newSurface, splitPane, toggleZoom, workspace, surface, refresh, openPanel, toggleSidebar, dialog]);

  const terminalHeader = (
    <div className="toolbar">
        <button className="icon-btn" onClick={() => splitPane("vertical")} title="Split right (Ctrl+D)">▢▢</button>
        <button className="icon-btn" onClick={() => splitPane("horizontal")} title="Split down (Ctrl+Shift+D)">⊟</button>
        <div style={{ position: "relative" }}>
          <button className="icon-btn" onClick={(e) => { e.stopPropagation(); setShellMenuOpen((v) => !v); }} title="Open pane with shell">⌹▾</button>
          {shellMenuOpen && (
            <div className="menu-dropdown" onClick={(e) => e.stopPropagation()}>
              {shells.map((s) => (<div key={s.path} className="menu-dropdown-item" onClick={() => { setShellMenuOpen(false); openWithShell(s.path); }}><span>{s.name}</span></div>))}
              <div className="terminal-context-sep" />
              <div className="menu-dropdown-item" onClick={() => { setShellMenuOpen(false); openBrowserPane(); }}><span>Browser</span></div>
              {shells.length === 0 && <div className="menu-dropdown-item" onClick={() => { setShellMenuOpen(false); openWithShell(); }}><span>Default shell</span></div>}
            </div>
          )}
        </div>
        <button className="icon-btn" onClick={() => applyLayout(2, 1)} title="Layout: 2 Columns">▥</button>
        <button className="icon-btn" onClick={() => applyLayout(2, 2)} title="Layout: Grid 2x2">▦</button>
        <button className="icon-btn" onClick={applyMainStackLayout} title="Layout: Main + Stack">▤</button>
        <button className="icon-btn" onClick={equalizeSurface} disabled={!canArrangePanes} title={canArrangePanes ? "Equalize panes" : "Equalize panes (needs 2+ panes)"}>≋</button>
        <button className={"icon-btn" + (zoomedPaneId ? " active-toggle" : "")} onClick={toggleZoom} disabled={!canArrangePanes} title={canArrangePanes ? "Zoom pane (Ctrl+Shift+Z)" : "Zoom pane (needs 2+ panes)"}>⤢</button>
        <button className={"icon-btn" + (broadcast ? " active-toggle" : "")} onClick={() => setBroadcast((v) => !v)} title="Broadcast input (Ctrl+Alt+B)">⌗</button>
        <div style={{ flex: 1 }} />
        <div className="tab-search">
          <span className="tab-search-icon">⌕</span>
          <input ref={searchInputRef} value={searchTerm}
            onChange={(e) => { setSearchTerm(e.target.value); terminalBus.search(focusedPaneId, e.target.value); }}
            onKeyDown={(e) => {
              if (e.key === "Enter") { e.preventDefault(); terminalBus.search(focusedPaneId, searchTerm, { back: e.shiftKey }); }
              else if (e.key === "Escape") { e.preventDefault(); setSearchTerm(""); terminalBus.clearSearch(focusedPaneId); searchInputRef.current?.blur(); }
            }}
          />
          <button className="icon-btn" onClick={() => terminalBus.search(focusedPaneId, searchTerm, { back: true })} title="Previous match">∧</button>
          <button className="icon-btn" onClick={() => terminalBus.search(focusedPaneId, searchTerm)} title="Next match">∨</button>
        </div>
      </div>
  );

  const dockTheme = (settings?.uiThemeName === "Light") ? "dockview-theme-light" : "dockview-theme-abyss";

  if (!state) return <div className="loading">Loading wimux...</div>;

  return (
    <div
      className="app-shell"
      onClick={() => { if (openMenu) setOpenMenu(null); if (surfaceMenu) setSurfaceMenu(null); if (shellMenuOpen) setShellMenuOpen(false); }}
    >
      <div className="menubar">
        <span className="menubar-brand">wimux</span>
        {Object.keys(menus).map((m) => (
          <div key={m} style={{ position: "relative" }}>
            <button className={"menu-item" + (openMenu === m ? " open" : "")}
              onClick={(e) => { e.stopPropagation(); setOpenMenu(openMenu === m ? null : m); }}
              onMouseEnter={() => { if (openMenu) setOpenMenu(m); }}>{m}</button>
            {openMenu === m && (
              <div className="menu-dropdown">
                {menus[m].map((it) => (
                  <div key={it.label} className="menu-dropdown-item" onClick={(e) => { e.stopPropagation(); setOpenMenu(null); it.run(); }}>
                    <span>{it.label}</span>{it.hint && <span className="menu-hint">{it.hint}</span>}
                  </div>
                ))}
              </div>
            )}
          </div>
        ))}
      </div>
      <div className="app">
      <main className="main">
        <DockLayout
          openPanels={activePanels} uiTheme={dockTheme} state={state} workspace={workspace} surface={surface}
          zoomedPaneId={zoomedPaneId} focusedPaneId={focusedPaneId} focusedCwd={focusedCwd}
          theme={activeTheme} fontFamily={fontFamily} fontSize={fontSize} customColors={customColors} settings={settings}
          searchOpen={searchOpen} setSearchOpen={setSearchOpen} refresh={refresh} refreshUnread={refreshUnread}
          insertIntoFocused={insertIntoFocused} focusPane={focusPane} closePane={closePane}
          setPaneTitle={setPaneTitle} setPaneCwd={setPaneCwd} onTerminalNotify={onTerminalNotify}
          setPaneType={setPaneType} setRatio={setRatio} splitPane={splitPane} toggleZoom={toggleZoom}
          newSurface={newSurface} selectSurface={selectSurface} closeSurface={closeSurface} closePanel={closePanel} openPanel={openPanel}
          selectWorkspace={selectWorkspace} closeWorkspace={closeWorkspace} newWorkspace={newWorkspace}
          workspaceRenameRequest={workspaceRenameRequest}
          wsStatus={wsStatus} unread={unread} broadcast={broadcast}
          openNotifications={() => setOverlay("notifications")}
          terminalHeader={terminalHeader}
        />
      </main>
      {overlay === "palette" && <CommandPalette commands={commands} onClose={() => setOverlay(null)} />}
      {overlay === "settings" && <SettingsModal themes={themes} onClose={() => setOverlay(null)} onApplied={applySettings} />}
      {overlay === "notifications" && <NotificationsPanel onClose={() => setOverlay(null)} onChanged={refreshUnread} />}
      {overlay === "snippets" && <SnippetsPanel onClose={() => setOverlay(null)} onInsert={insertIntoFocused} />}
      {overlay === "history" && <HistoryPicker paneId={focusedPaneId} onClose={() => setOverlay(null)} onPick={insertIntoFocused} />}
      {overlay === "templates" && <TemplatesPanel onClose={() => setOverlay(null)} workspaceId={workspace?.id} workspaceName={workspace?.name} onApplied={refresh} />}
      {overlay === "quickOpen" && <QuickOpen root={focusedCwd} onClose={() => setOverlay(null)} onPick={(p) => {}} />}
    </div>
    </div>
  );
}
