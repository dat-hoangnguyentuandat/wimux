import { forwardRef, useCallback, useEffect, useImperativeHandle, useMemo, useRef, useState } from "react";

interface Props {
  url: string;
  tabId?: string;
  /** Fired when the live tab reports its real title/URL (used for the tab label). */
  onMeta?: (meta: { title: string; url: string }) => void;
}

const WS_BASE = () => {
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${location.host}/ws/browser/view`;
};

function cdpButton(b: number): string {
  switch (b) {
    case 0: return "left";
    case 1: return "middle";
    case 2: return "right";
    default: return "none";
  }
}

/**
 * Renders a remote real browser tab streamed from the local server via CDP
 * screencast, and forwards mouse/keyboard/scroll back to it. No CSP bypass:
 * this is remote control of a real tab, shown as a live image. Auto-reconnects
 * if the underlying browser is closed (the server relaunches it).
 *
 * Each BrowserView instance maps to its own Chrome tab on the server. We pin
 * a stable per-pane tabId so different panes never collapse onto the same tab,
 * and we ask the server to open a fresh tab (new=1) unless the parent supplied
 * an explicit tabId.
 */
export interface BrowserViewHandle {
  go(url: string): void;
  back(): void;
  forward(): void;
  reload(): void;
}

export const BrowserView = forwardRef<BrowserViewHandle, Props>(function BrowserView({ url, tabId, onMeta }: Props, ref) {
  const imgRef = useRef<HTMLImageElement>(null);
  const wrapRef = useRef<HTMLDivElement>(null);
  const socketRef = useRef<WebSocket | null>(null);
  const pendingMessagesRef = useRef<object[]>([]);
  const [status, setStatus] = useState<"connecting" | "live" | "error">("connecting");
  const [error, setError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  // Stable per-pane id so each BrowserView mounts its own Chrome tab.
  // Stable per-pane id from parent. If parent supplies a tabId, it stays across remounts.
  const paneTabId = useMemo(() => tabId ?? crypto.randomUUID(), [tabId]);
  const onMetaRef = useRef(onMeta);
  useEffect(() => { onMetaRef.current = onMeta; }, [onMeta]);

  const urlRef = useRef(url);
  useEffect(() => { urlRef.current = url; }, [url]);

  const flushPendingMessages = useCallback(() => {
    const ws = socketRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    const pending = pendingMessagesRef.current.splice(0);
    for (const msg of pending) ws.send(JSON.stringify(msg));
  }, []);

  const sendWs = useCallback((msg: object) => {
    const ws = socketRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(msg));
      return;
    }
    pendingMessagesRef.current.push(msg);
  }, []);

  useEffect(() => {
    let closed = false;
    let retries = 0;
    const MAX_RETRIES = 3;

    const sendViewport = () => {
      const el = wrapRef.current;
      const ws = socketRef.current;
      if (!el || !ws || ws.readyState !== WebSocket.OPEN) return;
      const rect = el.getBoundingClientRect();
      ws.send(JSON.stringify({
        t: "viewport",
        width: Math.round(rect.width),
        height: Math.round(rect.height),
        dpr: window.devicePixelRatio || 1,
      }));
    };

    const connect = () => {
      const params = new URLSearchParams();
      // Always use the URL captured at mount time for the initial connect, so
      // editing the address bar does not cause a reconnect.
      params.set("url", urlRef.current);
      params.set("tabId", paneTabId);
      // Each pane mounts its own Chrome tab; never reuse an existing one.
      if (!tabId) params.set("new", "1");
      const ws = new WebSocket(`${WS_BASE()}?${params.toString()}`);
      socketRef.current = ws;
      setStatus("connecting");
      setError(null);

      ws.onopen = () => flushPendingMessages();
      ws.onmessage = (e) => {
        const data = String(e.data);
        const type = data[0];
        const body = data.slice(1);
        if (type === "F") {
          if (imgRef.current) imgRef.current.src = `data:image/jpeg;base64,${body}`;
          setStatus("live");
        } else if (type === "M") {
          try {
            const meta = JSON.parse(body) as { title: string; url: string };
            onMetaRef.current?.(meta);
          } catch { /* ignore malformed meta */ }
        } else if (type === "R") {
          retries = 0;
          setStatus("live");
          sendViewport();
          flushPendingMessages();
        } else if (type === "E") {
          setError(body);
          setStatus("error");
        }
      };
      ws.onerror = () => {
        if (!closed) { setStatus("error"); setError("Connection lost."); }
      };
      ws.onclose = () => {
        if (closed) return;
        if (retries < MAX_RETRIES) {
          retries++;
          setTimeout(connect, 1500);
        } else {
          setStatus("error");
          setError("Browser closed. Click Reconnect to restart it.");
        }
      };
    };

    connect();

    const ro = new ResizeObserver(() => sendViewport());
    if (wrapRef.current) ro.observe(wrapRef.current);

    return () => {
      closed = true;
      ro.disconnect();
      try { socketRef.current?.close(); } catch { /* ignore */ }
      socketRef.current = null;
    };
  }, [flushPendingMessages, paneTabId, reloadKey, tabId]);

  // Imperative handle: parent calls go/back/forward/reload directly. We do
  // NOT auto-navigate when `url` changes because that races with the live
  // tab''s own history (Back/Forward) and would re-pin us to the latest
  // server-reported URL on every meta update.
  useImperativeHandle(ref, () => ({
    go: (u) => sendWs({ t: "navigate", url: u }),
    back: () => sendWs({ t: "back" }),
    forward: () => sendWs({ t: "forward" }),
    reload: () => sendWs({ t: "reload" }),
  }), [sendWs]);

  const toTabCoords = (e: React.MouseEvent) => {
    const img = imgRef.current;
    if (!img) return { x: 0, y: 0 };
    const rect = img.getBoundingClientRect();
    const scaleX = (img.naturalWidth || rect.width) / rect.width;
    const scaleY = (img.naturalHeight || rect.height) / rect.height;
    return {
      x: (e.clientX - rect.left) * scaleX,
      y: (e.clientY - rect.top) * scaleY,
    };
  };


  const onMouse = (type: string) => (e: React.MouseEvent) => {
    const { x, y } = toTabCoords(e);
    sendWs({ t: "mouse", type, x, y, button: cdpButton(e.button), clickCount: type === "mousePressed" ? (e.detail || 1) : 0 });
  };

  const onWheel = (e: React.WheelEvent) => {
    e.preventDefault();
    const { x, y } = toTabCoords(e);
    sendWs({ t: "wheel", x, y, deltaX: e.deltaX, deltaY: e.deltaY });
  };

  const onKey = (type: string) => (e: React.KeyboardEvent) => {
    if (["Tab", " ", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"].includes(e.key)) e.preventDefault();
    const printable = e.key.length === 1 ? e.key : undefined;
    sendWs({
      t: "key",
      type,
      key: e.key,
      code: e.code,
      keyCode: e.keyCode,
      text: type === "keyDown" ? printable : undefined,
    });
  };

  return (
    <div className="browser-view" ref={wrapRef}>
      {status !== "live" && (
        <div className="browser-view-overlay">
          {status === "connecting" && <div className="browser-view-spinner" />}
          {status === "error" && (
            <div className="browser-view-error-box">
              <span className="browser-view-error">{error || "Stream unavailable."}</span>
              <button className="web-view-switch" onClick={() => setReloadKey((k) => k + 1)}>
                Reconnect
              </button>
            </div>
          )}
        </div>
      )}
      <img
        ref={imgRef}
        className="browser-view-frame"
        alt=""
        tabIndex={0}
        draggable={false}
        onMouseDown={onMouse("mousePressed")}
        onMouseUp={onMouse("mouseReleased")}
        onMouseMove={onMouse("mouseMoved")}
        onWheel={onWheel}
        onKeyDown={onKey("keyDown")}
        onKeyUp={onKey("keyUp")}
        onContextMenu={(e) => e.preventDefault()}
      />
    </div>
  );
});









