import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { SearchAddon } from "@xterm/addon-search";
import "@xterm/xterm/css/xterm.css";
import { api, type TerminalTheme } from "../lib/api";
import { terminalBus } from "../lib/terminalBus";

function WritePopup({
  onSend,
  onClose,
  initialText,
  onDraftChange,
  position,
}: {
  onSend: (text: string) => void;
  onClose: () => void;
  initialText: string;
  onDraftChange: (text: string) => void;
  position?: { x: number; y: number } | null;
}) {
  const [text, setText] = useState(initialText);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const popupRef = useRef<HTMLDivElement>(null);
  const [placed, setPlaced] = useState(false);

  useEffect(() => {
    inputRef.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  useLayoutEffect(() => {
    if (placed) inputRef.current?.focus();
  }, [placed]);

  useLayoutEffect(() => {
    const el = popupRef.current;
    if (!el) return;
    const margin = 8;
    const rect = el.getBoundingClientRect();
    const anchorX = position?.x ?? window.innerWidth / 2;
    const anchorY = position?.y ?? window.innerHeight / 2;
    const preferredLeft = position ? anchorX + 8 : anchorX - rect.width / 2;
    const preferredTop = position ? anchorY + 8 : anchorY - rect.height / 2;
    const left = Math.max(margin, Math.min(preferredLeft, window.innerWidth - rect.width - margin));
    const top = Math.max(margin, Math.min(preferredTop, window.innerHeight - rect.height - margin));
    el.style.left = `${left}px`;
    el.style.top = `${top}px`;
    setPlaced(true);
  }, [position]);

  const send = () => {
    if (!text) return;
    onSend(text);
    onClose();
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  };

  return (
    <div className="write-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div
        ref={popupRef}
        className="write-popup"
        style={{ visibility: placed ? "visible" : "hidden" }}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <div className="write-popup-row">
          <textarea
            ref={inputRef}
            className="write-popup-input"
            value={text}
            onChange={(e) => {
              setText(e.target.value);
              onDraftChange(e.target.value);
            }}
            onKeyDown={onKeyDown}
            placeholder="Type here, Enter to send…"
            rows={1}
          />
          <button className="write-popup-send" onClick={send} disabled={!text} title="Send">
            <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
              <path d="M1 8L15 1L8 15L6 9L1 8Z" fill="currentColor" stroke="currentColor" strokeWidth="1" strokeLinejoin="round"/>
              <path d="M8 15L6 9L15 1" stroke="white" strokeWidth="0.5" fill="none"/>
            </svg>
          </button>
        </div>
      </div>
    </div>
  );
}

interface Props {
  paneId: string;
  cwd?: string;
  focused: boolean;
  theme?: TerminalTheme;
  fontFamily: string;
  fontSize: number;
  settings?: any;
  onFocusRequest: () => void;
  onTitle?: (title: string) => void;
  onCwd?: (cwd: string) => void;
  onBell?: () => void;
  onNotify?: () => void;
  onSearchRequest?: () => void;
  onSplitRight?: () => void;
  onSplitDown?: () => void;
  onZoom?: () => void;
  onClosePane?: () => void;
  onCapture?: () => void;
  customColors?: { background?: string; foreground?: string; cursor?: string; selection?: string };
}

type CustomColors = { background?: string; foreground?: string; cursor?: string; selection?: string };

function toCssColor(c?: string) {
  if (!c) return undefined;
  return c.length === 9 ? "#" + c.slice(3) : c;
}

function toXtermTheme(t?: TerminalTheme, custom?: CustomColors) {
  if (!t) return undefined;
  const p = t.palette;
  const pick = (val: string | undefined, fallback: string) =>
    val && val.trim() ? toCssColor(val) : toCssColor(fallback);
  return {
    background: pick(custom?.background, t.background),
    foreground: pick(custom?.foreground, t.foreground),
    cursor: pick(custom?.cursor, t.cursor),
    selectionBackground: pick(custom?.selection, t.selection),
    black: toCssColor(p[0]), red: toCssColor(p[1]), green: toCssColor(p[2]), yellow: toCssColor(p[3]),
    blue: toCssColor(p[4]), magenta: toCssColor(p[5]), cyan: toCssColor(p[6]), white: toCssColor(p[7]),
    brightBlack: toCssColor(p[8]), brightRed: toCssColor(p[9]), brightGreen: toCssColor(p[10]), brightYellow: toCssColor(p[11]),
    brightBlue: toCssColor(p[12]), brightMagenta: toCssColor(p[13]), brightCyan: toCssColor(p[14]), brightWhite: toCssColor(p[15]),
  };
}

export function TerminalPane(props: Props) {
  const { paneId, cwd, focused, theme, fontFamily, fontSize, customColors } = props;
  const paneRef = useRef<HTMLDivElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null);
  const [measured, setMeasured] = useState(false);
  const [writeOpen, setWriteOpen] = useState(false);
  const [writePos, setWritePos] = useState<{ x: number; y: number } | null>(null);
  const [writeDraft, setWriteDraft] = useState("");
  const menuRef = useRef<HTMLDivElement>(null);
  // Anchor position for the quick-write button. Set on left-click inside
  // the typeable region (button === 0, no TUI mouse tracking). Cleared
  // when the user clicks the button itself or when the WritePopup opens.
  // The button does NOT follow the cursor — it stays at the click point
  // until the user clicks elsewhere, so the cursor can never "chase"
  // the button away before the user can click it.
  const [anchorPos, setAnchorPos] = useState<{ x: number; y: number } | null>(null);
  // True while the running TUI has DEC mouse tracking (1000/1002/1003) on.
  // Mirrors `TerminalBuffer.MouseEnabled` on the server; the server pushes a
  // `mouseTracking` event whenever it changes. Used to decide whether plain
  // right-click should open wimux's context menu: TUI apps swallow the click
  // so we must intercept; pwsh/cmd don't track so the browser default is
  // fine and we only force the menu on Shift+Right.
  const [mouseTracking, setMouseTracking] = useState(false);
  // Live mirrors read inside the mount-once effect's event handlers. That
  // effect only re-runs on `paneId` change, so its closures would otherwise
  // capture stale state/props; refs let the right-click handlers see the
  // current mouse-tracking mode and the RightClickAlwaysMenu setting.
  const mouseTrackingRef = useRef(false);
  const rightClickAlwaysMenuRef = useRef(false);
  const quickWriteEnabledRef = useRef(true);
  const lastTypeablePointerRef = useRef<{ clientX: number; clientY: number } | null>(null);

  // Default to the legacy behaviour (TUI apps keep their own right-click =
  // paste; Shift+Right opens wimux's menu). Only opt into "always menu" when the
  // setting is explicitly true. Missing/undefined (older settings.json or
  // before settings have loaded) keeps the legacy default.
  useEffect(() => {
    rightClickAlwaysMenuRef.current = props.settings?.rightClickAlwaysMenu === true;
  }, [props.settings]);

  useEffect(() => {
    const enabled = props.settings?.quickWriteEnabled !== false;
    quickWriteEnabledRef.current = enabled;
    if (!enabled) {
      setAnchorPos(null);
      setWriteOpen(false);
    }
  }, [props.settings]);

  const writeInput = (text: string) => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    const enc = (s: string) => btoa(String.fromCharCode(...new TextEncoder().encode(s)));
    ws.send("i" + enc(text));
  };

  const copySelection = async () => {
    const selected = termRef.current?.getSelection();
    if (selected) await navigator.clipboard.writeText(selected).catch(() => {});
    termRef.current?.clearSelection();
  };

  const pasteClipboard = async () => {
    const text = await navigator.clipboard.readText().catch(() => "");
    if (text) termRef.current?.paste(text);
  };

  const clearTerminal = () => {
    termRef.current?.clear();
    writeInput("\x0c");
  };

  const openWriteAt = (x: number, y: number) => {
    setWritePos({ x, y });
    setAnchorPos(null);
    setWriteOpen(true);
  };

  useEffect(() => {
    const term = new Terminal({
      fontFamily: `${fontFamily}, "Cascadia Code", Consolas, monospace`,
      fontSize,
      cursorBlink: true,
      allowProposedApi: true,
      theme: toXtermTheme(theme, customColors),
    });
    const fit = new FitAddon();
    const search = new SearchAddon();
    term.loadAddon(fit);
    term.loadAddon(search);
    term.loadAddon(new WebLinksAddon());
    term.open(containerRef.current!);
    fit.fit();
    termRef.current = term;
    fitRef.current = fit;

    // Intercept Ctrl+C / Ctrl+Insert at the xterm key-event layer. Default
    // xterm.js forwards every key combo into `onData` as raw bytes, which
    // means Ctrl+C is delivered to the pty as ETX (0x03) — pwsh interprets
    // that as SIGINT and kills the foreground pipeline, even when the user
    // only meant to copy the highlighted selection. The standard terminal
    // convention (xterm, iTerm2, VSCode's terminal, Windows Terminal) is:
    //   - selection non-empty + Ctrl+C  →  copy selection to clipboard
    //   - selection empty    + Ctrl+C  →  forward SIGINT
    // We can't rely on `window.getSelection()` because xterm draws to a
    // canvas and keeps its selection in an internal buffer — only
    // `term.getSelection()` reflects the actual highlight. Returning
    // `false` from the handler tells xterm to swallow the keypress, so the
    // bytes never reach `onData` / the WebSocket. Returning `true` lets
    // xterm convert it to its default sequence (Ctrl+C → "\x03").
    let lastCopiedAt = 0;
    term.attachCustomKeyEventHandler((e) => {
      if (e.type !== "keydown") return true;
      if (
        quickWriteEnabledRef.current &&
        !mouseTrackingRef.current &&
        e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey &&
        (e.key === "W" || e.key === "w")
      ) {
        const lastPointer = lastTypeablePointerRef.current;
        const r = paneRef.current?.getBoundingClientRect();
        e.preventDefault();
        e.stopPropagation();
        props.onFocusRequest();
        openWriteAt(
          lastPointer?.clientX ?? (r ? r.left + Math.min(r.width / 2, Math.max(24, r.width - 24)) : window.innerWidth / 2),
          lastPointer?.clientY ?? (r ? r.top + Math.min(r.height / 2, Math.max(24, r.height - 24)) : window.innerHeight / 2)
        );
        return false;
      }
      // Plain Ctrl+C / Ctrl+Insert — copy if a selection exists, else let
      // xterm deliver ETX to the pty. Skip combos that carry Shift/Alt/Meta
      // because those are different shortcuts in TUIs (e.g. Shift+Tab).
      if (
        e.ctrlKey &&!e.altKey &&!e.metaKey &&
        ((e.key === "c" || e.key === "C") || e.key === "Insert")
      ) {
        const sel = term.getSelection();
        if (sel) {
          // De-dupe: Chromium fires `keydown` twice on some IMEs/OSes when
          // Ctrl+C is held. Compare against the last copy timestamp instead
          // of clearing selection (clearing would surprise users who want to
          // inspect the selection after copying).
          const now = Date.now();
          if (now - lastCopiedAt < 50) {
            e.preventDefault();
            e.stopPropagation();
            return false;
          }
          lastCopiedAt = now;
          navigator.clipboard.writeText(sel).catch(() => {});
          e.preventDefault();
          e.stopPropagation();
          return false; // never delivered as ETX
        }
      }
      // Ctrl+Shift+C → explicit copy even without a (visible) selection —
      // matches the convention from gnome-terminal / xterm. We still call
      // getSelection() because highlight on a canvas isn't visible to
      // window.getSelection().
      if (e.ctrlKey && e.shiftKey &&!e.altKey &&!e.metaKey &&
          (e.key === "C" || e.key === "c")) {
        const sel = term.getSelection();
        if (sel) {
          navigator.clipboard.writeText(sel).catch(() => {});
          e.preventDefault();
          e.stopPropagation();
          return false;
        }
      }
      // Ctrl+V should paste clipboard text into the terminal. If it falls
      // through as the raw Ctrl+V control byte, TUIs like codex/claude-code
      // treat it as their own shortcut instead of receiving pasted text.
      if (
        (e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey &&
        (e.key === "v" || e.key === "V")
      ) {
        e.preventDefault();
        e.stopPropagation();
        navigator.clipboard.readText()
          .then((text) => { if (text) term.paste(text); })
          .catch(() => {});
        return false;
      }
      return true; // every other key flows through normally
    });

    const proto = location.protocol === "https:" ? "wss" : "ws";
    const params = new URLSearchParams({ cols: String(term.cols), rows: String(term.rows) });
    if (cwd) params.set("cwd", cwd);
    const ws = new WebSocket(`${proto}://${location.host}/ws/terminal/${paneId}?${params}`);
    wsRef.current = ws;

    const enc = (s: string) => btoa(String.fromCharCode(...new TextEncoder().encode(s)));
    const dec = (b64: string) => {
      const bin = atob(b64);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      return bytes;
    };

    ws.onmessage = (e) => {
      const msg = typeof e.data === "string" ? e.data : "";
      if (!msg) return;
      const kind = msg[0];
      const body = msg.slice(1);
      if (kind === "o") {
        term.write(dec(body));
      } else if (kind === "e") {
        try {
          const ev = JSON.parse(body);
          if (ev.type === "title" && ev.data) props.onTitle?.(ev.data);
          if (ev.type === "cwd" && ev.data) props.onCwd?.(ev.data);
          if (ev.type === "bell") { term.write("\x07"); props.onBell?.(); }
          if (ev.type === "notify") props.onNotify?.();
          if (ev.type === "mouseTracking") {
            const on = ev.data === "1";
            setMouseTracking(on);
            mouseTrackingRef.current = on;
          }
        } catch { /* ignore */ }
      }
    };

    const sendTerminalInput = (text: string) => {
      if (!text) return;
      if (ws.readyState === WebSocket.OPEN) ws.send("i" + enc(text));
      terminalBus.broadcastFrom(paneId, text);
    };

    // Forward every xterm `onData` chunk straight to the server. xterm.js
    // owns the hidden textarea and already binds keypress + IME composition
    // (Windows TSF on Edge/Chrome will commit precomposed Vietnamese runes
    // to the hidden textarea; xterm surfaces them as onData strings).
    // We deliberately do NOT add our own textarea or composition listeners:
    // doing so double-sent every keystroke (the original "duplicate" bug
    // when typing into codex). We also do NOT run a JS-side Telex/VNI
    // composer — raw keystroke interception fights the OS IME and produces
    // the wrong bytes for xterm's composition surface. The OS TSF IME
    // already emits the right "\b<composed>" sequence, and the server's
    // ConPTY forwards it verbatim. This matches the wimux2 (WPF) behavior
    // of trusting the host's text input.
    term.onData((data) => { sendTerminalInput(data); });

    const unregister = terminalBus.register(paneId, {
      write: (text) => { if (ws.readyState === WebSocket.OPEN) ws.send("i" + enc(text)); },
      search: (term2, opts) => { if (opts?.back) search.findPrevious(term2); else search.findNext(term2); },
      clearSearch: () => search.clearDecorations(),
    });

    const sendResize = () => {
      if (ws.readyState === WebSocket.OPEN) ws.send(`r${term.cols},${term.rows}`);
    };
    term.onResize(sendResize);
    ws.onopen = sendResize;

    const ro = new ResizeObserver(() => {
      try { fit.fit(); } catch { /* not visible */ }
    });
    ro.observe(containerRef.current!);

    // xterm.js installs its own capture-phase mousedown / contextmenu handlers
    // on `term.element` and calls `preventDefault()` + `stopPropagation()` in
    // tracking mode. So plain `addEventListener` on the wrapper div is too
    // late — those bubbles never reach it. We mirror wimux2's
    // `OnMouseRightButtonDown` flow by attaching on the *xterm* element with
    // `{ capture: true }`, so we run before xterm can suppress the event.
    const xtermEl = term.element ?? containerRef.current!;
    // Right-click handling. Two routes converge on the same menu:
    //   1. `mousedown` with button=2 — fires for every right-click.
    //   2. `contextmenu` — browser's synthetic event. In TUI mode (xterm
    //      captures the click + sends SGR report) it never fires, so path
    //      (1) is the only one that runs. In pwsh mode xterm forwards the
    //      event unchanged, so path (2) is what actually runs.
    // Default (RightClickAlwaysMenu = false, legacy): in plain shells a plain
    // right-click opens wimux's context menu; inside a mouse-tracking TUI the
    // plain right-click is forwarded to the TUI (its own "paste") and only
    // Shift+Right opens wimux's menu. When RightClickAlwaysMenu = true, a plain
    // right-click opens wimux's menu everywhere — we intercept the click in the
    // capture phase (before xterm can forward it to the TUI) via
    // preventDefault() + stopPropagation().
    const openMenu = (e: MouseEvent) => {
      e.preventDefault();
      e.stopPropagation();
      props.onFocusRequest();
      setMeasured(false);
      setMenu({ x: e.clientX, y: e.clientY });
    };
    const onRightMouseDown = (e: MouseEvent) => {
      if (e.button !== 2) return; // wimux2 only handles right-button down
      // Legacy mode: let the TUI handle a plain right-click while it is
      // tracking the mouse; only Shift+Right forces wimux's menu.
      if (!rightClickAlwaysMenuRef.current && mouseTrackingRef.current && !e.shiftKey) return;
      openMenu(e);
    };
    const onContextMenu = (e: MouseEvent) => {
      if (!rightClickAlwaysMenuRef.current && mouseTrackingRef.current && !e.shiftKey) return;
      openMenu(e);
    };

    // Register the right-click interceptors on the WRAPPER (parent of the
    // xterm element), NOT on xtermEl itself. The capture phase dispatches
    // top-down (ancestor → target), so the wrapper's capture listeners fire
    // BEFORE any capture listener xterm installed on its own element. By
    // calling stopPropagation() inside openMenu() we prevent the event from
    // ever reaching xterm, so xterm cannot forward the right-click to the TUI
    // as a DEC mouse report — which is what claude-code / codex / cline
    // interpret as "paste". (Registering on xtermEl would run AFTER xterm's
    // own capture handler, so the paste report would already have been sent.)
    const wrapperEl = containerRef.current!;
    wrapperEl.addEventListener("mousedown", onRightMouseDown, { capture: true });
    wrapperEl.addEventListener("contextmenu", onContextMenu, { capture: true });

    // Global click listener for quick-write anchoring.
    // Bypasses xterm event blocking by listening at document level.
    const onDocumentClick = (e: MouseEvent) => {
      if (e.button !== 0) return;
      if (!quickWriteEnabledRef.current) return;
      if (mouseTrackingRef.current) return;
      if (!containerRef.current || !containerRef.current.contains(e.target as Node)) return;
      lastTypeablePointerRef.current = { clientX: e.clientX, clientY: e.clientY };
      const r = (paneRef.current ?? containerRef.current).getBoundingClientRect();
      setAnchorPos({ x: e.clientX - r.left, y: e.clientY - r.top });
      props.onFocusRequest();
    };
    const onDocumentPointerMove = (e: PointerEvent) => {
      if (!quickWriteEnabledRef.current) return;
      if (mouseTrackingRef.current) return;
      if (!containerRef.current || !containerRef.current.contains(e.target as Node)) return;
      lastTypeablePointerRef.current = { clientX: e.clientX, clientY: e.clientY };
    };
    document.addEventListener('click', onDocumentClick);
    document.addEventListener('pointermove', onDocumentPointerMove);

    return () => {
      document.removeEventListener('click', onDocumentClick);
      document.removeEventListener('pointermove', onDocumentPointerMove);
      unregister();
      ro.disconnect();
      wrapperEl.removeEventListener("mousedown", onRightMouseDown, { capture: true } as any);
      wrapperEl.removeEventListener("contextmenu", onContextMenu, { capture: true } as any);
      setAnchorPos(null);
      ws.close();
      term.dispose();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paneId]);

  useEffect(() => {
    if (termRef.current) {
      termRef.current.options.theme = toXtermTheme(theme, customColors);
      termRef.current.options.fontFamily = `${fontFamily}, "Cascadia Code", Consolas, monospace`;
      termRef.current.options.fontSize = fontSize;
      try { fitRef.current?.fit(); } catch { /* */ }
    }
  }, [theme, fontFamily, fontSize, customColors]);

  useEffect(() => {
    if (focused) {
      termRef.current?.focus();
      try { fitRef.current?.fit(); } catch { /* */ }
    } else {
      // Focus moved to another pane — clear the quick-write anchor so its
      // button doesn't keep showing in this (now background) pane. Without
      // this, anchoring in pane A then clicking pane B leaves A's button
      // visible, and both panes end up showing a button at once.
      setAnchorPos(null);
    }
  }, [focused]);

  // Position the context menu synchronously, BEFORE the browser paints, so it
  // never flashes at the cursor and never visibly jumps to its final spot.
  // (The old 2-RAF approach painted the menu at the cursor first, which read
  // as a jarring "jump" — worst near the bottom edge where it flips above.)
  // `visibility: hidden` (see the JSX below) keeps it invisible until this
  // layout effect has measured and repositioned it; setMeasured(true) then
  // reveals it on the same paint, already at the right place.
  useLayoutEffect(() => {
    if (!menu) return;
    const el = menuRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const margin = 8;
    const vh = window.innerHeight;
    const vw = window.innerWidth;
    const cursorX = menu.x;
    const cursorY = menu.y;
    const menuH = rect.height;
    const menuW = rect.width;
    const spaceAbove = cursorY - margin;
    const spaceBelow = vh - cursorY - margin;
    // Keep the menu above the cursor when there's room; otherwise drop below;
    // if neither fits, clamp to the roomier side and let it scroll.
    let top: number;
    let maxHeight: number | undefined;
    let overflowY: string | undefined;
    if (menuH <= spaceAbove) {
      top = cursorY - menuH - margin;
    } else if (menuH <= spaceBelow) {
      top = cursorY + margin;
    } else {
      if (spaceAbove >= spaceBelow) {
        top = margin;
        maxHeight = Math.max(80, spaceAbove);
      } else {
        top = cursorY + margin;
        maxHeight = Math.max(80, spaceBelow);
      }
      overflowY = "auto";
    }
    let left: number;
    if (cursorX + menuW <= vw - margin) {
      left = cursorX;
    } else if (cursorX - menuW >= margin) {
      left = cursorX - menuW;
    } else {
      left = Math.max(margin, Math.min(cursorX, vw - menuW - margin));
    }
    el.style.top = `${top}px`;
    el.style.left = `${left}px`;
    el.style.right = "";
    el.style.bottom = "";
    el.style.transform = "";
    el.style.maxHeight = maxHeight != null ? `${maxHeight}px` : "";
    el.style.overflowY = overflowY ?? "";
    setMeasured(true);
  }, [menu]);

  useEffect(() => {
    if (!menu) return;
    const close = () => setMenu(null);
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    window.addEventListener("mousedown", close);
    window.addEventListener("keydown", onKey);
    return () => {
      window.removeEventListener("mousedown", close);
      window.removeEventListener("keydown", onKey);
    };
  }, [menu]);

  const chooseFile = async () => {
    try {
      const picked = await api.chooseFile(cwd);
      if (picked?.path) {
        termRef.current?.paste(picked.path);
        window.requestAnimationFrame(() => {
          termRef.current?.focus();
          props.onFocusRequest();
        });
      }
    } catch {
      // Browser file inputs cannot expose real full paths, so if the native
      // local dialog is unavailable there is no useful fallback here.
    }
  };

  const runMenuAction = (action: () => void | Promise<void>) => {
    setMenu(null);
    void action();
  };

  const sendWritePopup = (text: string) => {
    writeInput(text);
    setWriteDraft("");
    window.requestAnimationFrame(() => {
      window.requestAnimationFrame(() => {
        termRef.current?.focus();
        try { fitRef.current?.fit(); } catch { /* terminal may be hidden */ }
      });
    });
  };

  return (
    <>
      <div
        ref={paneRef}
        className={"term-pane" + (focused ? " focused" : "")}
        style={{
          width: "100%",
          height: "100%",
          background: toCssColor(customColors?.background || theme?.background),
        }}
        onMouseDown={(e) => {
          // Anchor quick-write button on left-click. React synthetic event
          // runs after DOM event completes, so xterm's capture handler can't block it.
          if (e.button !== 0) return;
          if (!quickWriteEnabledRef.current) return;
          if (mouseTrackingRef.current) return;
          lastTypeablePointerRef.current = { clientX: e.clientX, clientY: e.clientY };
          const r = paneRef.current!.getBoundingClientRect();
          setAnchorPos({ x: e.clientX - r.left, y: e.clientY - r.top });
          props.onFocusRequest();
        }}
      >
        <div ref={containerRef} className="terminal-xterm-host" />
        {props.settings?.quickWriteEnabled !== false && anchorPos && !writeOpen && !menu && (
          <button
            className="quick-write-button"
            style={{ left: anchorPos.x - 18, top: anchorPos.y - 36 }}
            title="Quick write"
            aria-label="Open quick write"
            // stopPropagation on pointerdown so xterm's capture-phase mousedown
            // handler (line ~380) doesn't fire — that handler would re-anchor
            // the button to the icon's own coordinates immediately after the
            // click, then the click would open the popup, but the button would
            // briefly flicker to the icon's position before disappearing.
            // stopPropagation on click prevents the same re-anchor race on the
            // subsequent click event.
            onPointerDown={(e) => e.stopPropagation()}
            onMouseDown={(e) => e.stopPropagation()}
            onClick={(e) => {
              e.stopPropagation();
              openWriteAt(e.clientX, e.clientY);
            }}
          >
            <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true">
              <path d="M3 11.8V13h1.2L11.7 5.5 10.5 4.3 3 11.8z" stroke="currentColor" strokeWidth="1.25" strokeLinecap="round" strokeLinejoin="round"/>
              <path d="M9.8 3.6 10.7 2.7a1.15 1.15 0 0 1 1.6 1.6l-.9.9" stroke="currentColor" strokeWidth="1.25" strokeLinecap="round" strokeLinejoin="round"/>
            </svg>
          </button>
        )}
      </div>
      {menu && (
        <div
          ref={menuRef}
          className="terminal-context-menu"
          style={{
            left: menu.x,
            top: menu.y,
            visibility: measured ? "visible" : "hidden",
          }}
          onMouseDown={(e) => e.stopPropagation()}
        >
          <button onClick={() => runMenuAction(copySelection)}>Copy<span>Ctrl+C</span></button>
          <button onClick={() => runMenuAction(pasteClipboard)}>Paste<span>Ctrl+V</span></button>
          <button onClick={() => runMenuAction(() => termRef.current?.selectAll())}>Select All</button>
          <button onClick={() => runMenuAction(chooseFile)}>Choose File</button>
          <div className="terminal-context-sep" />
          <button onClick={() => runMenuAction(() => props.onSplitRight?.())}>Split Right<span>Ctrl+D</span></button>
          <button onClick={() => runMenuAction(() => props.onSplitDown?.())}>Split Down<span>Ctrl+Shift+D</span></button>
          <button onClick={() => runMenuAction(() => props.onZoom?.())}>Zoom Pane<span>Ctrl+Shift+Z</span></button>
          <button className="danger" onClick={() => runMenuAction(() => props.onClosePane?.())}>Close Pane</button>
          <div className="terminal-context-sep" />
          <button onClick={() => runMenuAction(() => props.onCapture?.())}>Capture Transcript</button>
          <button onClick={() => runMenuAction(clearTerminal)}>Clear Terminal</button>
          <button onClick={() => runMenuAction(() => props.onSearchRequest?.())}>Search<span>Ctrl+Shift+F</span></button>
        </div>
      )}
      {writeOpen && (
        <WritePopup
          onSend={sendWritePopup}
          onClose={() => setWriteOpen(false)}
          initialText={writeDraft}
          onDraftChange={setWriteDraft}
          position={writePos}
        />
      )}
    </>
  );
}




