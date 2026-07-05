import { useEffect, useMemo, useRef, useState } from "react";
import { api } from "../lib/api";

interface Props {
  paneId?: string;
  onClose: () => void;
  onPick: (command: string) => void;
}

export function HistoryPicker({ paneId, onClose, onPick }: Props) {
  const [all, setAll] = useState<string[]>([]);
  const [query, setQuery] = useState("");
  const [index, setIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { api.getHistory(paneId).then(setAll); inputRef.current?.focus(); }, [paneId]);

  const filtered = useMemo(() => {
    const seen = new Set<string>();
    const out: string[] = [];
    for (let i = all.length - 1; i >= 0; i--) {
      const c = all[i];
      if (seen.has(c)) continue;
      seen.add(c);
      if (c.toLowerCase().includes(query.toLowerCase())) out.push(c);
    }
    return out;
  }, [all, query]);
  const clamped = Math.min(index, Math.max(0, filtered.length - 1));

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") onClose();
    else if (e.key === "ArrowDown") { e.preventDefault(); setIndex((i) => Math.min(i + 1, filtered.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setIndex((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter") {
      e.preventDefault();
      const c = filtered[clamped];
      if (c) { if (e.shiftKey) { onPick(c); onClose(); } else { onPick(c + "\r"); onClose(); } }
    }
    else if (e.key === "c" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      const c = filtered[clamped];
      if (c) navigator.clipboard?.writeText(c).catch(() => {});
    }
  };

  return (
    <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="wimux-popup-panel" onMouseDown={(e) => e.stopPropagation()} style={{ maxWidth: 620, maxHeight: 560 }}>
        <div className="wimux-panel-toolbar">
          <div className="wimux-panel-toolbar-row">
            <span className="wimux-panel-title">COMMAND HISTORY{paneId ? ` · ${paneId.slice(0, 8)}` : ""}</span>
            <span className="wimux-spacer" />
            <button className="wimux-icon-btn" onClick={onClose}>×</button>
          </div>
        </div>
        <div className="wimux-panel-toolbar" style={{ borderTop: "none" }}>
          <div className="wimux-panel-toolbar-row">
            <input ref={inputRef} style={{ flex: 1 }} placeholder="Search history (Enter = run, Shift+Enter = insert)..."
              value={query} onChange={(e) => { setQuery(e.target.value); setIndex(0); }} onKeyDown={onKey} />
          </div>
        </div>
        <div className="wimux-panel-body" style={{ maxHeight: 400, padding: 0, overflow: "auto" }}>
          <table className="wimux-grid">
            <thead>
              <tr><th style={{ width: 56 }}>#</th><th>Command</th></tr>
            </thead>
            <tbody>
              {filtered.map((c, i) => (
                <tr key={i} className={i === clamped ? "wimux-row-active" : ""} onMouseEnter={() => setIndex(i)} onClick={() => { onPick(c + "\r"); onClose(); }}>
                  <td className="dim mono">{i + 1}</td>
                  <td className="mono">{c}</td>
                </tr>
              ))}
              {filtered.length === 0 && <tr><td colSpan={2} className="wimux-empty">No history</td></tr>}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
