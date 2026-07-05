export type PaneType = "terminal" | "web" | "notepad";

export interface Pane {
  id: string;
  type: PaneType;
  title?: string;
  workingDirectory?: string;
  url?: string;
  notes?: string;
}

export interface SplitNode {
  id: string;
  isLeaf: boolean;
  direction: "vertical" | "horizontal";
  splitRatio: number;
  paneId?: string;
  first?: SplitNode;
  second?: SplitNode;
}

export interface Surface {
  id: string;
  name: string;
  root: SplitNode;
  focusedPaneId?: string;
  panes: Record<string, Pane>;
}

export interface Workspace {
  id: string;
  name: string;
  accentColor: string;
  workingDirectory?: string;
  surfaces: Surface[];
  selectedSurfaceId?: string;
}

export interface AppState {
  version: number;
  workspaces: Workspace[];
  selectedWorkspaceId?: string;
}

export interface TerminalTheme {
  name: string;
  background: string;
  foreground: string;
  cursor: string;
  selection: string;
  palette: string[];
}

const j = { "Content-Type": "application/json" };

async function req<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, init);
  if (!res.ok) throw new Error(`${init?.method ?? "GET"} ${url} -> ${res.status}`);
  const text = await res.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

export const api = {
  getState: () => req<AppState>("/api/state"),
  getThemes: () => req<TerminalTheme[]>("/api/themes"),
  getSettings: () => req<any>("/api/settings"),
  saveSettings: (s: any) => req<any>("/api/settings", { method: "PUT", headers: j, body: JSON.stringify(s) }),
  getShells: () => req<{ name: string; path: string }[]>("/api/shells"),

  createWorkspace: (name?: string, workingDirectory?: string) =>
    req<Workspace>("/api/workspaces", { method: "POST", headers: j, body: JSON.stringify({ name, workingDirectory }) }),
  deleteWorkspace: (id: string) => req<void>(`/api/workspaces/${id}`, { method: "DELETE" }),
  selectWorkspace: (id: string) => req<void>(`/api/workspaces/${id}/select`, { method: "POST" }),
  updateWorkspace: (id: string, patch: { name?: string; accentColor?: string; workingDirectory?: string }) =>
    req<Workspace>(`/api/workspaces/${id}`, { method: "PUT", headers: j, body: JSON.stringify(patch) }),

  createSurface: (wsId: string, name?: string, shell?: string, type?: PaneType, url?: string) =>
    req<Surface>(`/api/workspaces/${wsId}/surfaces`, { method: "POST", headers: j, body: JSON.stringify({ name, shell, type, url }) }),
  selectSurface: (wsId: string, sId: string) =>
    req<void>(`/api/workspaces/${wsId}/surfaces/${sId}/select`, { method: "POST" }),
  renameSurface: (wsId: string, sId: string, name: string) =>
    req<Surface>(`/api/workspaces/${wsId}/surfaces/${sId}`, { method: "PUT", headers: j, body: JSON.stringify({ name }) }),
  deleteSurface: (wsId: string, sId: string) =>
    req<void>(`/api/workspaces/${wsId}/surfaces/${sId}`, { method: "DELETE" }),

  split: (wsId: string, sId: string, paneId: string, direction: "vertical" | "horizontal", shell?: string, type?: PaneType, url?: string) =>
    req<Surface>(`/api/workspaces/${wsId}/surfaces/${sId}/split`, { method: "POST", headers: j, body: JSON.stringify({ paneId, direction, shell, type, url }) }),
  closePane: (wsId: string, sId: string, paneId: string) =>
    req<Surface>(`/api/workspaces/${wsId}/surfaces/${sId}/panes/${paneId}`, { method: "DELETE" }),
  focusPane: (wsId: string, sId: string, paneId: string) =>
    req<void>(`/api/workspaces/${wsId}/surfaces/${sId}/focus/${paneId}`, { method: "POST" }),
  setRatio: (wsId: string, sId: string, nodeId: string, ratio: number) =>
    req<void>(`/api/workspaces/${wsId}/surfaces/${sId}/ratio`, { method: "POST", headers: j, body: JSON.stringify({ nodeId, ratio }) }),

  // ── Notifications ──────────────────────────────────────────────
  getNotifications: () => req<{ items: Notification[]; unread: number }>("/api/notifications"),
  getWorkspaceStatus: () => req<WorkspaceStatus[]>("/api/workspaces/status"),
  markNotificationRead: (id: string) => req<void>(`/api/notifications/${id}/read`, { method: "POST" }),
  markAllNotificationsRead: () => req<void>("/api/notifications/read-all", { method: "POST" }),
  clearNotifications: () => req<void>("/api/notifications", { method: "DELETE" }),

  // ── Command logs / history / transcripts ───────────────────────
  getLogDates: () => req<string[]>("/api/logs/dates"),
  getLogs: (opts?: { date?: string; q?: string }) => {
    const p = new URLSearchParams();
    if (opts?.date) p.set("date", opts.date);
    if (opts?.q) p.set("q", opts.q);
    return req<CommandLogEntry[]>(`/api/logs?${p}`);
  },
  getHistory: (paneId?: string) =>
    req<string[]>(`/api/history${paneId ? `?paneId=${paneId}` : ""}`),
  getTranscripts: () => req<TranscriptEntry[]>("/api/transcripts"),
  getTranscriptContent: (path: string) =>
    fetch(`/api/transcripts/content?path=${encodeURIComponent(path)}`).then((r) => r.text()),
  capturePane: (paneId: string) =>
    req<{ file: string }>(`/api/panes/${paneId}/capture`, { method: "POST" }),

  // ── Snippets ───────────────────────────────────────────────────
  getSnippets: (q?: string) => req<Snippet[]>(`/api/snippets${q ? `?q=${encodeURIComponent(q)}` : ""}`),
  getSnippetCategories: () => req<string[]>("/api/snippets/categories"),
  createSnippet: (s: Partial<Snippet>) =>
    req<Snippet>("/api/snippets", { method: "POST", headers: j, body: JSON.stringify(s) }),
  updateSnippet: (id: string, s: Snippet) =>
    req<Snippet>(`/api/snippets/${id}`, { method: "PUT", headers: j, body: JSON.stringify(s) }),
  deleteSnippet: (id: string) => req<void>(`/api/snippets/${id}`, { method: "DELETE" }),
  useSnippet: (id: string) => req<void>(`/api/snippets/${id}/use`, { method: "POST" }),

  // ── Templates ──────────────────────────────────────────────────
  getTemplates: () => req<WorkspaceTemplate[]>("/api/templates"),
  saveTemplate: (t: WorkspaceTemplate) =>
    req<WorkspaceTemplate>("/api/templates", { method: "POST", headers: j, body: JSON.stringify(t) }),
  deleteTemplate: (id: string) => req<void>(`/api/templates/${id}`, { method: "DELETE" }),
  saveTemplateFromWorkspace: (wsId: string, name: string) =>
    req<WorkspaceTemplate>(`/api/templates/from-workspace/${wsId}`, { method: "POST", headers: j, body: JSON.stringify({ name }) }),
  applyTemplate: (id: string) => req<Workspace>(`/api/templates/${id}/apply`, { method: "POST" }),

  // ── Agent runtime ──────────────────────────────────────────────
  getAgentSettings: () => req<any>("/api/agent/settings"),
  saveAgentSettings: (s: any) => req<any>("/api/agent/settings", { method: "PUT", headers: j, body: JSON.stringify(s) }),
  setAgentSecret: (name: string, value: string | null) =>
    req<void>("/api/agent/secret", { method: "PUT", headers: j, body: JSON.stringify({ name, value }) }),
  clearAgentSecret: (name: string) =>
    req<void>("/api/agent/secret", { method: "PUT", headers: j, body: JSON.stringify({ name, value: null }) }),
  sendAgentPrompt: (paneId: string, prompt: string, threadId?: string) =>
    req<{ ok: boolean; threadId?: string; error?: string }>("/api/agent/send", { method: "POST", headers: j, body: JSON.stringify({ paneId, prompt, threadId }) }),

  // ── Agent threads ──────────────────────────────────────────────
  getThreads: (opts?: { workspaceId?: string; surfaceId?: string; paneId?: string; q?: string }) => {
    const p = new URLSearchParams();
    if (opts?.workspaceId) p.set("workspaceId", opts.workspaceId);
    if (opts?.surfaceId) p.set("surfaceId", opts.surfaceId);
    if (opts?.paneId) p.set("paneId", opts.paneId);
    if (opts?.q) p.set("q", opts.q);
    const qs = p.toString();
    return req<AgentThread[]>(`/api/threads${qs ? `?${qs}` : ""}`);
  },
  getThreadMessages: (threadId: string) => req<AgentMessage[]>(`/api/threads/${threadId}/messages`),
  createThread: (paneId: string) =>
    req<AgentThread>("/api/threads", { method: "POST", headers: j, body: JSON.stringify({ paneId }) }),
  activateThread: (threadId: string, opts?: { workspaceId?: string; surfaceId?: string; paneId?: string }) =>
    req<void>(`/api/threads/${threadId}/activate`, { method: "POST", headers: j, body: JSON.stringify(opts || {}) }),
  deleteThread: (threadId: string) => req<void>(`/api/threads/${threadId}`, { method: "DELETE" }),
  deleteThreadMessage: (threadId: string, messageId: string) =>
    req<void>(`/api/threads/${threadId}/messages/${messageId}`, { method: "DELETE" }),

  // ── Quota ──────────────────────────────────────────────────────
  getQuota: () => req<QuotaSnapshot>("/api/quota"),

  // ── Git / ports ────────────────────────────────────────────────
  getGitBranch: (cwd?: string) =>
    req<{ branch?: string; remote?: string }>(`/api/git/branch${cwd ? `?cwd=${encodeURIComponent(cwd)}` : ""}`),
  getPorts: (paneId: string) => req<number[]>(`/api/ports?paneId=${paneId}`),
  closeClientTab: (clientTabId: string) =>
    req<void>(`/api/browser/client-tab/${encodeURIComponent(clientTabId)}`, { method: "DELETE", keepalive: true }),
  forgetClientTab: (clientTabId: string) =>
    req<void>(`/api/browser/client-tab/${encodeURIComponent(clientTabId)}/binding`, { method: "DELETE", keepalive: true }),
  cleanupOrphanBrowserTabs: () =>
    req<void>("/api/browser/cleanup-orphans", { method: "POST", keepalive: true }),
  canEmbed: (url: string) =>
    req<{ canEmbed: boolean; reason: string; xFrameOptions?: string }>(`/api/browser/can-embed?url=${encodeURIComponent(url)}`),
  updatePane: (wsId: string, sId: string, paneId: string, patch: { type?: string; url?: string; notes?: string }) =>
    req<Pane>(`/api/workspaces/${wsId}/surfaces/${sId}/panes/${paneId}`, { method: "PUT", headers: j, body: JSON.stringify(patch) }),

  // ── External agents ────────────────────────────────────────────
  getAgents: () => req<ExternalAgent[]>("/api/agents"),
  getAgentConversation: (sessionFilePath: string, max?: number) =>
    req<{ role: string; content: string; timestamp: string }[]>(
      `/api/agents/conversation?sessionFilePath=${encodeURIComponent(sessionFilePath)}${max ? `&max=${max}` : ""}`),
  sendExternalAgentMessage: (agent: { pid: number; projectPath?: string }, text: string) =>
    req<{ ok: boolean; paneId?: string; error?: string }>("/api/agents/send", {
      method: "POST",
      headers: j,
      body: JSON.stringify({ pid: agent.pid, projectPath: agent.projectPath, text }),
    }),

  // ── Workspace env / ssh ────────────────────────────────────────
  getWorkspaceEnv: (id: string) => req<Record<string, string>>(`/api/workspaces/${id}/env`),
  setWorkspaceEnv: (id: string, env: Record<string, string>) =>
    req<Record<string, string>>(`/api/workspaces/${id}/env`, { method: "PUT", headers: j, body: JSON.stringify(env) }),
  chooseFile: (initialDirectory?: string) =>
    req<{ path: string } | undefined>(`/api/dialog/open-file${initialDirectory ? `?initialDirectory=${encodeURIComponent(initialDirectory)}` : ""}`, { method: "POST" }),
  setClipboardImageFile: (path: string) =>
    req<void>("/api/clipboard/image-file", { method: "POST", headers: j, body: JSON.stringify({ path }) }),
  quickOpen: (root: string, q?: string) =>
    req<{ fullPath: string; name: string }[]>(`/api/quick-open?root=${encodeURIComponent(root)}${q ? `&q=${encodeURIComponent(q)}` : ""}`),
  getWorkspaceSsh: (id: string) => req<SshProfile[]>(`/api/workspaces/${id}/ssh`),
  setWorkspaceSsh: (id: string, profiles: SshProfile[]) =>
    req<SshProfile[]>(`/api/workspaces/${id}/ssh`, { method: "PUT", headers: j, body: JSON.stringify(profiles) }),
};

export interface ExternalAgent {
  name: string;
  type: number;
  status: number;
  summary: string;
  pid: number;
  projectPath: string;
  sessionId: string;
  lastActive: string;
  sessionFilePath?: string;
  typeLabel: string;
  statusLabel: string;
}

export interface SshProfile {
  id: string;
  name: string;
  host: string;
  port: number;
  user: string;
  identityFile?: string;
}

export interface WorkspaceStatus { id: string; workingDirectory?: string; branch?: string; unread: number; }

export interface Notification {
  id: string;
  workspaceId: string;
  surfaceId: string;
  paneId?: string;
  isRead: boolean;
  title: string;
  subtitle?: string;
  body: string;
  timestamp: string;
  source: number;
}

export interface CommandLogEntry {
  id: string;
  paneId: string;
  surfaceId: string;
  workspaceId: string;
  command?: string;
  startedAt: string;
  completedAt?: string;
  exitCode?: number;
  workingDirectory?: string;
  durationDisplay: string;
}

export interface TranscriptEntry {
  filePath: string;
  fileName: string;
  capturedAt: string;
  workspaceId: string;
  surfaceId: string;
  paneId: string;
  workingDirectory?: string;
  reason: string;
  sizeBytes: number;
}

export interface Snippet {
  id: string;
  name: string;
  content: string;
  category: string;
  tags: string[];
  description?: string;
  useCount: number;
  isFavorite: boolean;
}

export interface WorkspaceTemplate {
  id: string;
  name: string;
  description: string;
  surfaces: { name: string; panes: { shell?: string; workingDirectory?: string; direction: number }[] }[];
  environmentVariables: Record<string, string>;
}

export interface QuotaRow {
  provider: string;
  model: string;
  requests: number;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  lastActivityLocal: string;
}

export interface AgentThread {
  id: string;
  workspaceId: string;
  surfaceId: string;
  paneId: string;
  agentName: string;
  title: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  messageCount: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalTokens: number;
  compactionCount: number;
  lastMessagePreview: string;
}

export interface AgentMessage {
  id: string;
  threadId: string;
  createdAtUtc: string;
  role: string;
  content: string;
  provider: string;
  model: string;
  toolName: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  isCompactionSummary: boolean;
}

export interface QuotaSnapshot {
  generatedAtUtc: string;
  windows: Record<string, { rows: QuotaRow[]; totalTokens: number; requests: number }>;
}











