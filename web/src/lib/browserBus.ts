// Singleton WebSocket client for the native Browser Manager on the local server.
// The frontend never controls Chrome directly; it sends compact commands here and
// receives tab state via server-pushed events (no polling).

export interface BrowserTabState {
  id: string;
  title: string;
  url: string;
  favicon?: string;
  isLoading: boolean;
  isActive: boolean;
}

type TabsListener = (tabs: BrowserTabState[]) => void;
type ErrorListener = (message: string) => void;

const WS_URL = () => {
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${location.host}/ws/browser`;
};

class BrowserBus {
  private socket: WebSocket | null = null;
  private tabs: BrowserTabState[] = [];
  private tabListeners = new Set<TabsListener>();
  private errorListeners = new Set<ErrorListener>();
  private reconnectTimer: number | null = null;
  private queue: string[] = [];

  private ensureSocket() {
    if (this.socket && (this.socket.readyState === WebSocket.OPEN || this.socket.readyState === WebSocket.CONNECTING))
      return;
    const ws = new WebSocket(WS_URL());
    this.socket = ws;
    ws.onopen = () => {
      for (const m of this.queue) ws.send(m);
      this.queue = [];
      ws.send("s"); // request a fresh sync on connect
    };
    ws.onmessage = (e) => this.onMessage(String(e.data));
    ws.onclose = () => {
      this.socket = null;
      this.scheduleReconnect();
    };
    ws.onerror = () => { try { ws.close(); } catch { /* ignore */ } };
  }

  private scheduleReconnect() {
    if (this.reconnectTimer !== null) return;
    this.reconnectTimer = window.setTimeout(() => {
      this.reconnectTimer = null;
      if (this.tabListeners.size > 0) this.ensureSocket();
    }, 2000);
  }

  private onMessage(raw: string) {
    if (!raw) return;
    const type = raw[0];
    const body = raw.slice(1);
    try {
      if (type === "L") {
        this.tabs = JSON.parse(body) as BrowserTabState[];
        this.emitTabs();
      } else if (type === "O") {
        const tab = body ? (JSON.parse(body) as BrowserTabState) : null;
        if (tab) this.upsert(tab);
      } else if (type === "E") {
        for (const l of this.errorListeners) l(body);
      } else {
        // Event envelope: { evt, payload }
        const env = JSON.parse(raw) as { evt: string; payload: unknown };
        this.applyEvent(env.evt, env.payload);
      }
    } catch {
      // Malformed frame; ignore rather than break the stream.
    }
  }

  private applyEvent(evt: string, payload: unknown) {
    switch (evt) {
      case "browser:all-tabs":
        this.tabs = payload as BrowserTabState[];
        this.emitTabs();
        break;
      case "browser:tab-opened":
      case "browser:tab-updated":
      case "browser:tab-focused":
        if (payload && typeof payload === "object" && "id" in (payload as object))
          this.upsert(payload as BrowserTabState);
        break;
      case "browser:tab-closed": {
        const id = (payload as { id?: string })?.id;
        if (id) {
          this.tabs = this.tabs.filter((t) => t.id !== id);
          this.emitTabs();
        }
        break;
      }
      case "browser:error":
        for (const l of this.errorListeners) l(String(payload));
        break;
    }
  }

  private upsert(tab: BrowserTabState) {
    const idx = this.tabs.findIndex((t) => t.id === tab.id);
    if (idx >= 0) this.tabs[idx] = tab;
    else this.tabs = [...this.tabs, tab];
    if (tab.isActive) this.tabs = this.tabs.map((t) => ({ ...t, isActive: t.id === tab.id }));
    this.emitTabs();
  }

  private emitTabs() {
    const snapshot = [...this.tabs];
    for (const l of this.tabListeners) l(snapshot);
  }

  private send(msg: string) {
    this.ensureSocket();
    if (this.socket && this.socket.readyState === WebSocket.OPEN) this.socket.send(msg);
    else this.queue.push(msg);
  }

  // ── Public API ────────────────────────────────────────────────────

  onTabs(listener: TabsListener): () => void {
    this.tabListeners.add(listener);
    this.ensureSocket();
    listener([...this.tabs]);
    return () => {
      this.tabListeners.delete(listener);
      if (this.tabListeners.size === 0 && this.socket) {
        try { this.socket.close(); } catch { /* ignore */ }
        this.socket = null;
      }
    };
  }

  onError(listener: ErrorListener): () => void {
    this.errorListeners.add(listener);
    return () => { this.errorListeners.delete(listener); };
  }

  getTabs(): BrowserTabState[] { return [...this.tabs]; }

  open(url: string) { this.send("c" + url); }
  focus(tabId: string) { this.send("f" + tabId); }
  close(tabId: string) { this.send("x" + tabId); }
  reload(tabId: string) { this.send("r" + tabId); }
  sync() { this.send("s"); }
}

export const browserBus = new BrowserBus();
