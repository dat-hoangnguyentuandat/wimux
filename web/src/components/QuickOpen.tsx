import { useEffect, useRef, useState } from "react";
import { api } from "../lib/api";

interface Props {
  root: string;
  onClose: () => void;
  onPick: (fullPath: string) => void;
}

export function QuickOpen({ root, onClose, onPick }: Props) {
  const [query, setQuery] = useState("");
  const [items, setItems] = useState<{ fullPath: string; name: string }[]>([]);
  const [index, setIndex] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { inputRef.current?.focus(); }, []);
  useEffect(() => {
    if (!root) return;
    const t = setTimeout(() => { api.quickOpen(root, query).then((r) => { setItems(r.slice(0, 50)); setIndex(0); }).catch(() => setItems([])); }, 120);
    return () => clearTimeout(t);
  }, [root, query]);

  const clamped = Math.min(index, Math.max(0, items.length - 1));
  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") onClose();
    else if (e.key === "ArrowDown") { e.preventDefault(); setIndex((i) => Math.min(i + 1, items.length - 1)); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setIndex((i) => Math.max(i - 1, 0)); }
    else if (e.key === "Enter") { e.preventDefault(); const it = items[clamped]; if (it) { onClose(); onPick(it.fullPath); } }
  };

  return (
    <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="wimux-popup-panel" onMouseDown={(e) => e.stopPropagation()} style={{ maxWidth: 560, maxHeight: 480 }}>
        <div className="wimux-panel-toolbar">
          <div className="wimux-panel-toolbar-row">
            <span className="wimux-search-icon">⌕</span>
            <input ref={inputRef} style={{ flex: 1 }} placeholder={root ? "Quick open file..." : "No folder for active pane"}
              value={query} onChange={(e) => setQuery(e.target.value)} onKeyDown={onKey} />
          </div>
        </div>
        <div className="wimux-panel-body" style={{ maxHeight: 400, padding: 0, overflow: "auto" }}>
          {items.length === 0 && <div className="wimux-empty">No files</div>}
          {items.map((it, i) => (
            <div key={it.fullPath}
              className={"wimux-quickopen-item" + (i === clamped ? " active" : "")}
              onMouseEnter={() => setIndex(i)} onClick={() => { onClose(); onPick(it.fullPath); }}>
              <span className="wimux-quickopen-name">{it.name}</span>
              <span className="wimux-quickopen-path dim mono">{it.fullPath}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
