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
  adoptCdpTabId?: string;
  openerTabId?: string;
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
const YOUTUBE_HOME = "wimux://youtube";
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
  if (target.startsWith("wimux://")) return target;
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

function LiveBrowserTabView({
  paneId,
  tab,
  active,
  rawUrl,
  onHandle,
  onMeta,
  onPopup,
  onPopupClosed,
}: {
  paneId: string;
  tab: BrowserTab;
  active: boolean;
  rawUrl: string;
  onHandle: (tabId: string, handle: BrowserViewHandle | null) => void;
  onMeta: (tabId: string, meta: { title: string; url: string }) => void;
  onPopup: (popup: { cdpTabId: string; url: string }) => void;
  onPopupClosed: (popup: { cdpTabId: string }) => void;
}) {
  const ref = useCallback((handle: BrowserViewHandle | null) => {
    onHandle(tab.id, handle);
  }, [onHandle, tab.id]);
  const meta = useCallback((value: { title: string; url: string }) => {
    onMeta(tab.id, value);
  }, [onMeta, tab.id]);

  return (
    <div
      className="web-view-host"
      style={{ display: active ? "block" : "none" }}
    >
      <BrowserView
        key={`${paneId}-${tab.id}`}
        ref={ref}
        url={rawUrl}
        tabId={tab.browserTabId}
        adoptCdpTabId={tab.adoptCdpTabId}
        onMeta={meta}
        onPopup={onPopup}
        onPopupClosed={onPopupClosed}
      />
    </div>
  );
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
  const liveRefs = useRef<Record<string, BrowserViewHandle | null>>({});
  const dragIdRef = useRef<string | null>(null);
  const adoptedPopupIdsRef = useRef(new Set<string>());
  const activeIdRef = useRef(activeId);

  const active = useMemo(() => tabs.find((t) => t.id === activeId) ?? tabs[0], [tabs, activeId]);
  useEffect(() => { activeIdRef.current = activeId; }, [activeId]);

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
  const applyMetaForTab = useCallback((tabId: string, meta: { title: string; url: string }) => {
    const nextUrl = meta.url?.trim();
    setTabs((rows) => rows.map((tab) => {
      if (tab.id !== tabId) return tab;
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
  }, [persistPaneUrl]);

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
    const activeLive = liveRefs.current[active.id];
    if (activeLive) activeLive.go(target);
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

  const closeTab = (id: string, opts?: { closeRemote?: boolean }) => {
    const closeRemote = opts?.closeRemote !== false;
    const removed = tabs.find((t) => t.id === id);
    if (removed) {
      if (removed.adoptCdpTabId) adoptedPopupIdsRef.current.delete(removed.adoptCdpTabId);
      // Close the underlying live Chrome tab so the host browser doesn't keep
      // running a tab the user has no UI for. (The unmount cleanup only fires
      // when the whole WebPane is torn down.)
      if (closeRemote) {
        try { api.closeClientTab(removed.browserTabId); } catch { /* ignore */ }
      } else {
        try { api.forgetClientTab(removed.browserTabId); } catch { /* ignore */ }
      }
    }
    setTabs((rows) => {
      if (rows.length === 1) {
        const replacement = newTab(HOME_URL);
        setActiveId(replacement.id);
        persistPaneUrl(replacement.url);
        return [replacement];
      }
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
    const activeLive = liveRefs.current[active.id];
    if (activeLive) {
      if (delta < 0) activeLive.back();
      else activeLive.forward();
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
    const activeLive = liveRefs.current[active.id];
    if (activeLive) { activeLive.reload(); return; }
    patchActive((tab) => ({ ...tab, frameKey: tab.frameKey + 1 }));
  };

  const setLiveHandle = useCallback((tabId: string, h: BrowserViewHandle | null) => {
    liveRefs.current[tabId] = h;
    if (!h) return;
    if (activeIdRef.current === tabId) h.focus();
    setTabs((rows) => {
      const target = rows.find((tab) => tab.id === tabId);
      if (!target || target.liveSession) return rows;
      return rows.map((tab) => (
        tab.id === tabId ? { ...tab, liveSession: true } : tab
      ));
    });
  }, []);

  useEffect(() => {
    liveRefs.current[activeId]?.focus();
  }, [activeId]);

  const openPopupTab = useCallback((popup: { cdpTabId: string; url: string }) => {
    if (adoptedPopupIdsRef.current.has(popup.cdpTabId)) {
      const existing = tabsRef.current.find((tab) => tab.adoptCdpTabId === popup.cdpTabId);
      if (existing) setActiveId(existing.id);
      return;
    }
    const existing = tabsRef.current.find((tab) => tab.adoptCdpTabId === popup.cdpTabId);
    if (existing) {
      setActiveId(existing.id);
      return;
    }
    adoptedPopupIdsRef.current.add(popup.cdpTabId);
    const popupUrl = popup.url || "about:blank";
    const tab: BrowserTab = {
      ...newTab(popupUrl),
      browserTabId: crypto.randomUUID(),
      adoptCdpTabId: popup.cdpTabId,
      openerTabId: activeIdRef.current,
      liveSession: true,
    };
    setTabs((rows) => [...rows, tab]);
    setActiveId(tab.id);
    persistPaneUrl(popupUrl);
  }, [persistPaneUrl]);

  const closePopupTab = useCallback((popup: { cdpTabId: string }) => {
    const tab = tabsRef.current.find((row) => row.adoptCdpTabId === popup.cdpTabId);
    if (!tab) return;
    adoptedPopupIdsRef.current.delete(popup.cdpTabId);
    liveRefs.current[tab.id] = null;
    setTabs((rows) => {
      const next = rows.filter((row) => row.id !== tab.id);
      if (next.length === rows.length) return rows;
      if (activeIdRef.current === tab.id) {
        const opener = tab.openerTabId ? next.find((row) => row.id === tab.openerTabId) : undefined;
        setActiveId((opener ?? next[0])?.id ?? activeIdRef.current);
      }
      return next.length > 0 ? next : rows;
    });
    try { api.forgetClientTab(tab.browserTabId); } catch { /* target already closed */ }
  }, []);

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

  const rawUrlForTab = (tab: BrowserTab) => {
    if (tab.url.startsWith("/api/frame-proxy?")) {
      return new URL(tab.url, location.origin).searchParams.get("url") ?? tab.url;
    }
    if (tab.url === YOUTUBE_HOME) return tab.draft.startsWith("http") ? tab.draft : tab.url;
    return tab.url;
  };

  const liveTabs = tabs.filter((tab) => tab.liveSession || (tab.id === active.id && effectiveRenderMode === "blocked"));

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
      <div className="web-content-stack">
        {liveTabs.map((tab) => (
          <LiveBrowserTabView
            key={`${paneId}-${tab.id}`}
            paneId={paneId}
            tab={tab}
            active={tab.id === active.id}
            rawUrl={rawUrlForTab(tab)}
            onHandle={setLiveHandle}
            onMeta={applyMetaForTab}
            onPopup={openPopupTab}
            onPopupClosed={closePopupTab}
          />
        ))}
        {effectiveRenderMode !== "blocked" && (
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
    </div>
  );
}























