import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api } from "../lib/api";
import { BrowserView, type BrowserViewHandle } from "./browser/BrowserView";

interface Props {
  wsId: string;
  sId: string;
  paneId: string;
  url?: string;
}

interface BrowserTab {
  id: string;
  // Stable id for the underlying Chrome tab; persists across remounts so we don't lose context.
  browserTabId: string;
  title: string;
  url: string;
  draft: string;
  history: string[];
  index: number;
  frameKey: number;
  // Once true, this tab stays mounted to the live BrowserView until closed,
  // so Back/Forward/Reload/Enter keep driving the same Chrome tab even when
  // the user navigates to a site that the local classifier thinks is iframable.
  liveSession: boolean;
}

const HOME_URL = "https://www.google.com/webhp?igu=1";
const SEARCH_URL = "https://www.google.com/search?igu=1&q=";
const YOUTUBE_HOME = "cmux://youtube";
const DIRECT_HOSTS = new Set([
  "www.youtube-nocookie.com",
  "youtube-nocookie.com",
]);

// URLs on this set are known to block iframes (X-Frame-Options / CSP).
// The BrowserCard is shown instead of attempting an iframe render.
const BLOCKED_HOSTS = new Set([
  "google.com",
  "www.google.com",
  "accounts.google.com",
  "youtube.com",
  "www.youtube.com",
  "youtu.be",
  "tiktok.com",
  "www.tiktok.com",
  "facebook.com",
  "www.facebook.com",
  "instagram.com",
  "www.instagram.com",
  "twitter.com",
  "www.twitter.com",
  "x.com",
  "www.x.com",
  "threads.net",
  "www.threads.net",
]);

function proxiedUrl(target: string) {
  return `/api/frame-proxy?url=${encodeURIComponent(target)}`;
}

function youtubeEmbedUrl(u: URL) {
  let id = "";
  if (u.hostname === "youtu.be") {
    id = u.pathname.split("/").filter(Boolean)[0] ?? "";
  } else if (u.pathname === "/watch") {
    id = u.searchParams.get("v") ?? "";
  } else if (u.pathname.startsWith("/shorts/")) {
    id = u.pathname.split("/").filter(Boolean)[1] ?? "";
  } else if (u.pathname.startsWith("/embed/")) {
    id = u.pathname.split("/").filter(Boolean)[1] ?? "";
  }
  return id ? `https://www.youtube-nocookie.com/embed/${id}` : "";
}

/** Returns true when the hostname is known to block iframes. */
function isKnownBlocked(hostname: string) {
  return BLOCKED_HOSTS.has(hostname) || BLOCKED_HOSTS.has("www." + hostname);
}

/**
 * Determines how to render a URL:
 *  "iframe"     – render directly (DIRECT_HOSTS, or localhost-style)
 *  "embedded"   – render through the frame-proxy (most sites)
 *  "blocked"    – skip iframe; show BrowserCard (known CSP/frame-busting sites)
 */
type RenderMode = "iframe" | "embedded" | "blocked";

function classifyUrl(target: string): RenderMode {
  try {
    const u = new URL(target);
    if (DIRECT_HOSTS.has(u.hostname)) return "iframe";
    if (isKnownBlocked(u.hostname)) return "blocked";
    // Treat localhost / private-network hosts as iframe.
    if (u.hostname === "localhost" || u.hostname === "127.0.0.1" || u.hostname.endsWith(".local")) return "iframe";
    return "embedded";
  } catch {
    return "embedded";
  }
}

function normalizeUrl(value: string) {
  let target = value.trim();
  if (!target) return "";
  if (target.startsWith("/api/frame-proxy?")) return target;
  if (target.startsWith("cmux://")) return target;
  if (!/^https?:\/\//i.test(target)) {
    const hostPart = target.split(/[/?#]/, 1)[0] ?? "";
    const looksLikeAddress =
      !/\s/.test(target) &&
      (hostPart === "localhost" ||
        hostPart.includes(".") ||
        /^\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?$/.test(hostPart));
    if (!looksLikeAddress) return SEARCH_URL + encodeURIComponent(target);
    target = "https://" + target;
  }
  try {
    const u = new URL(target);
    if ((u.hostname === "google.com" || u.hostname === "www.google.com") && (u.pathname === "/" || u.pathname === "")) {
      return HOME_URL;
    }
    if (u.hostname === "youtu.be" || u.hostname.endsWith(".youtube.com")) {
      const embed = youtubeEmbedUrl(u);
      if (embed) return embed;
      return YOUTUBE_HOME;
    }
  } catch {
    // Let the iframe/browser surface try the normalized string.
  }
  return target;
}

function titleFor(url: string) {
  try {
    if (url.startsWith("/api/frame-proxy?")) {
      const target = new URL(url, location.origin).searchParams.get("url") ?? url;
      return titleFor(target);
    }
    if (url === YOUTUBE_HOME) return "YouTube";
    const u = new URL(url);
    if (u.hostname.includes("google.")) return "Google";
    if (u.hostname.includes("youtube.")) return "YouTube";
    return u.hostname.replace(/^www\./, "");
  } catch {
    return "Browser";
  }
}

/** Clamp a tab label so very long page titles collapse to an ellipsis. */
function clampTitle(value: string, max = 40) {
  const t = value.trim();
  if (t.length <= max) return t;
  return t.slice(0, max - 1).trimEnd() + "…";
}

function newTab(url = HOME_URL): BrowserTab {
  return {
    liveSession: false,
    id: crypto.randomUUID(),
    browserTabId: crypto.randomUUID(),
    title: titleFor(url),
    url,
    draft: url,
    history: [url],
    index: 0,
    frameKey: 0,
  };
}

function extractRawUrl(normalizedOrProxied: string) {
  if (normalizedOrProxied.startsWith("/api/frame-proxy?")) {
    return new URL(normalizedOrProxied, location.origin).searchParams.get("url") ?? normalizedOrProxied;
  }
  return normalizedOrProxied;
}

export function WebPane({ wsId, sId, paneId, url }: Props) {
  const initialUrl = normalizeUrl(url || HOME_URL) || HOME_URL;
  const [tabs, setTabs] = useState<BrowserTab[]>(() => [newTab(initialUrl)]);
  const [activeId, setActiveId] = useState(() => tabs[0].id);
  const iframeRef = useRef<HTMLIFrameElement>(null);
  const liveRef = useRef<BrowserViewHandle | null>(null);
  const dragIdRef = useRef<string | null>(null);

  const active = useMemo(() => tabs.find((t) => t.id === activeId) ?? tabs[0], [tabs, activeId]);

  // How to render the active tab. Starts from a cheap local classification,
  // then refines via a server-side header probe (X-Frame-Options / CSP).
  const [renderMode, setRenderMode] = useState<RenderMode>(() => classifyUrl(extractRawUrl(active.url)));
  // Once a tab enters live (blocked) mode, keep it live until the user closes
  // the tab. This keeps Back/Forward/Reload/Enter wired to the live Chrome tab
  // even when the URL flips to something the local classifier thinks is
  // iframable, and prevents the BrowserView from unmounting mid-session.
  const effectiveRenderMode: RenderMode = active.liveSession ? "blocked" : renderMode;

  useEffect(() => {
    if (active.url === YOUTUBE_HOME) { setRenderMode("blocked"); return; }
    const raw = extractRawUrl(active.url);
    const local = classifyUrl(raw);
    // Direct/known hosts and localhost skip the probe.
    if (local !== "embedded") { setRenderMode(local); return; }
    let cancelled = false;
    setRenderMode("iframe"); // optimistic; the probe downgrades to "blocked" if needed
    api.canEmbed(raw)
      .then((r) => { if (!cancelled) setRenderMode(r.canEmbed ? "iframe" : "blocked"); })
      .catch(() => { if (!cancelled) setRenderMode("iframe"); });
    return () => { cancelled = true; };
  }, [active.url]);

  const patchActive = (fn: (tab: BrowserTab) => BrowserTab) => {
    setTabs((rows) => rows.map((tab) => (tab.id === active.id ? fn(tab) : tab)));
  };

  const persistPaneUrl = useCallback((nextUrl: string) => {
    api.updatePane(wsId, sId, paneId, { url: nextUrl }).catch(() => {});
  }, [wsId, sId, paneId]);

  // Live title/URL reported by the CDP-backed browser session, so the tab
  // label reflects the page the user is actually on (e.g. YouTube, not Google).
  const applyMeta = useCallback((meta: { title: string; url: string }) => {
    const nextUrl = meta.url?.trim();
    setTabs((rows) => rows.map((tab) => {
      if (tab.id !== active.id) return tab;
      const nextTitle = meta.title ? clampTitle(meta.title) : tab.title;
      if (!nextUrl || nextUrl === "about:blank") {
        return nextTitle === tab.title ? tab : { ...tab, title: nextTitle };
      }

      const currentUrl = tab.history[tab.index] ?? tab.url;
      let history = tab.history;
      let index = tab.index;
      if (nextUrl !== currentUrl) {
        const existingIndex = tab.history.lastIndexOf(nextUrl);
        if (existingIndex >= 0) {
          index = existingIndex;
        } else {
          history = [...tab.history.slice(0, tab.index + 1), nextUrl];
          index = history.length - 1;
        }
      }

      if (nextTitle === tab.title && nextUrl === tab.url && nextUrl === tab.draft && history === tab.history && index === tab.index) {
        return tab;
      }
      return { ...tab, title: nextTitle, url: nextUrl, draft: nextUrl, history, index, liveSession: true };
    }));
    if (nextUrl && nextUrl !== "about:blank") persistPaneUrl(nextUrl);
  }, [active.id, persistPaneUrl]);

  // When the entire web pane is torn down (user closed the split, switched
  // the pane type, deleted the workspace, etc.) close the real Chrome tab
  // so the live browser does not keep running on the host. Use a ref so the
  // cleanup does not depend on the live tabs list (which may be empty by
  // the time React fires the cleanup).
  const tabsRef = useRef<BrowserTab[]>([]);
  useEffect(() => { tabsRef.current = tabs; }, [tabs]);
  useEffect(() => () => {
    for (const t of tabsRef.current) {
      try { api.closeClientTab(t.browserTabId); } catch { /* ignore */ }
    }
  }, []);

  const go = () => {
    const target = normalizeUrl(active.draft);
    if (!target) return;
    if (liveRef.current) liveRef.current.go(target);
    patchActive((tab) => {
      const history = [...tab.history.slice(0, tab.index + 1), target];
      return { ...tab, title: titleFor(target), url: target, draft: target === YOUTUBE_HOME ? tab.draft : target, history, index: history.length - 1 };
    });
    persistPaneUrl(target);
  };

  const openYoutubeVideo = () => {
    const target = normalizeUrl(active.draft);
    if (!target || target === YOUTUBE_HOME) return;
    patchActive((tab) => {
      const history = [...tab.history.slice(0, tab.index + 1), target];
      return { ...tab, title: titleFor(target), url: target, draft: target, history, index: history.length - 1 };
    });
    persistPaneUrl(target);
  };

  const addTab = () => {
    const tab = newTab();
    setTabs((rows) => [...rows, tab]);
    setActiveId(tab.id);
    persistPaneUrl(tab.url);
  };

  const closeTab = (id: string) => {
    const removed = tabs.find((t) => t.id === id);
    if (removed) {
      // Close the underlying live Chrome tab so the host browser doesn't keep
      // running a tab the user has no UI for. (The unmount cleanup only fires
      // when the whole WebPane is torn down.)
      try { api.closeClientTab(removed.browserTabId); } catch { /* ignore */ }
    }
    setTabs((rows) => {
      if (rows.length === 1) return rows;
      const idx = rows.findIndex((t) => t.id === id);
      const next = rows.filter((t) => t.id !== id);
      if (id === activeId) {
        const nextActive = next[Math.max(0, idx - 1)] ?? next[0];
        setActiveId(nextActive.id);
        persistPaneUrl(nextActive.url);
      }
      return next;
    });
  };

  const moveHistory = (delta: 1 | -1) => {
    if (liveRef.current) {
      if (delta < 0) liveRef.current.back();
      else liveRef.current.forward();
      return;
    }
    const nextIndex = active.index + delta;
    if (nextIndex >= 0 && nextIndex < active.history.length) {
      const target = active.history[nextIndex];
      patchActive((tab) => ({ ...tab, title: titleFor(target), url: target, draft: target, index: nextIndex }));
      persistPaneUrl(target);
    }
    try {
      if (delta < 0) iframeRef.current?.contentWindow?.history.back();
      else iframeRef.current?.contentWindow?.history.forward();
    } catch {
      // Cross-origin frames may block direct history access; our stored URL still updates.
    }
  };

  const reload = () => {
    if (liveRef.current) { liveRef.current.reload(); return; }
    patchActive((tab) => ({ ...tab, frameKey: tab.frameKey + 1 }));
  };

  const setLiveHandle = useCallback((h: BrowserViewHandle | null) => {
    liveRef.current = h;
    if (!h) return;
    setTabs((rows) => rows.map((tab) => (
      tab.id === activeId && !tab.liveSession ? { ...tab, liveSession: true } : tab
    )));
  }, [activeId]);

  const reorder = (targetId: string) => {
    const sourceId = dragIdRef.current;
    if (!sourceId || sourceId === targetId) return;
    setTabs((rows) => {
      const sourceIndex = rows.findIndex((t) => t.id === sourceId);
      const targetIndex = rows.findIndex((t) => t.id === targetId);
      if (sourceIndex < 0 || targetIndex < 0) return rows;
      const next = [...rows];
      const [item] = next.splice(sourceIndex, 1);
      next.splice(targetIndex, 0, item);
      return next;
    });
  };

  // Build the raw URL for the BrowserCard (use the last history entry so we show
  // the real address, not the proxied one).
  const rawCardUrl = useMemo(() => {
    if (active.url.startsWith("/api/frame-proxy?")) {
      return new URL(active.url, location.origin).searchParams.get("url") ?? active.url;
    }
    if (active.url === YOUTUBE_HOME) {
      // Derive the original YouTube URL from the draft if possible.
      return active.draft.startsWith("http") ? active.draft : active.url;
    }
    return active.url;
  }, [active.url, active.draft]);

  return (
    <div className="web-pane">
      {/* ── Tab strip ── */}
      <div className="web-tabs">
        {tabs.map((tab) => (
          <div
            key={tab.id}
            className={"web-tab" + (tab.id === active.id ? " active" : "")}
            draggable
            onClick={() => { setActiveId(tab.id); persistPaneUrl(tab.url); }}
            onDragStart={() => { dragIdRef.current = tab.id; }}
            onDragOver={(e) => e.preventDefault()}
            onDrop={() => reorder(tab.id)}
          >
            <span>{tab.title}</span>
            <button onClick={(e) => { e.stopPropagation(); closeTab(tab.id); }} title="Close tab">x</button>
          </div>
        ))}
        <button className="web-tab-add" onClick={addTab} title="New tab">+</button>
      </div>

      {/* ── Address bar ── */}
      <div className="web-bar">
        <button className="web-icon-btn" onClick={() => moveHistory(-1)} title="Back">‹</button>
        <button className="web-icon-btn" onClick={() => moveHistory(1)} title="Forward">›</button>
        <button className="web-icon-btn" onClick={reload} title="Reload">↻</button>
        <input
          value={active.draft}
          placeholder="Search or enter address"
          onChange={(e) => patchActive((tab) => ({ ...tab, draft: e.target.value }))}
          onKeyDown={(e) => { if (e.key === "Enter") go(); }}
        />
        <button className="web-icon-btn" onClick={go} title="Search">⌕</button>
      </div>

      {/* ── Content ── */}
      {effectiveRenderMode === "blocked" ? (
        <div className="web-view-host">
          <BrowserView
            key={`${paneId}-${active.id}`}
            ref={setLiveHandle}
            url={rawCardUrl}
            tabId={active.browserTabId}
            onMeta={applyMeta}
          />
        </div>
      ) : (
        <iframe
          key={`${active.id}-${active.frameKey}-${active.url}`}
          ref={iframeRef}
          className="web-frame"
          src={active.url}
          title={active.title}
          sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-popups-to-escape-sandbox"
        />
      )}
    </div>
  );
}























