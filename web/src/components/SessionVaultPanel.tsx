import { useEffect, useMemo, useState } from "react";
import { api, type TranscriptEntry, type Workspace } from "../lib/api";

interface Props {
  workspace?: Workspace;
  onClose: () => void;
}

function shortId(id: string) {
  if (!id) return "-";
  return id.length <= 8 ? id : id.slice(0, 8);
}

function fmtEntity(id: string, name: string) {
  return name && name !== shortId(id) ? `${name} · ${shortId(id)}` : shortId(id);
}

export function SessionVaultPanel({ workspace, onClose }: Props) {
  const [items, setItems] = useState<TranscriptEntry[]>([]);
  const [selected, setSelected] = useState<TranscriptEntry | null>(null);
  const [content, setContent] = useState("");
  const [query, setQuery] = useState("");

  const load = () => api.getTranscripts().then(setItems);
  useEffect(() => { load(); }, []);

  useEffect(() => {
    if (selected) api.getTranscriptContent(selected.filePath).then(setContent);
    else setContent("");
  }, [selected]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return items;
    return items.filter((t) =>
      (t.fileName ?? "").toLowerCase().includes(q) ||
      (t.reason ?? "").toLowerCase().includes(q) ||
      (t.workingDirectory ?? "").toLowerCase().includes(q) ||
      (workspace?.name ?? "").toLowerCase().includes(q)
    );
  }, [items, query, workspace]);

  const copyAll = () => {
    if (content) navigator.clipboard?.writeText(content).catch(() => {});
  };
  const openFile = () => {
    if (!selected) return;
    const a = document.createElement("a");
    a.href = `/api/transcripts/content?path=${encodeURIComponent(selected.filePath)}`;
    a.download = selected.fileName ?? "transcript.txt";
    a.click();
  };

  return (
    <div className="cmux-panel">
      <div className="cmux-panel-toolbar">
        <div className="cmux-panel-toolbar-row">
          <label>Search</label>
          <input style={{ width: 320 }} value={query} onChange={(e) => setQuery(e.target.value)} placeholder="File, cwd, reason..." />
          <button className="cmux-btn" onClick={load}>Refresh</button>
          <span className="cmux-spacer" />
          <span className="dim">{items.length} entries</span>
        </div>
      </div>
      <div className="cmux-panel-body cmux-split-body cmux-wide-split-body">
        <div className="cmux-list">
          {filtered.length === 0 && <div className="cmux-empty">No captured transcripts</div>}
          {filtered.map((t) => (
            <div
              key={t.filePath}
              className={"cmux-list-item" + (selected?.filePath === t.filePath ? " active" : "")}
              onClick={() => setSelected(t)}
            >
              <div className="cmux-list-title mono">{new Date(t.capturedAt).toLocaleString()}</div>
              <div className="cmux-list-meta dim mono">
                {fmtEntity(t.workspaceId, workspace?.id === t.workspaceId ? workspace.name : "")} · {shortId(t.surfaceId)} · {shortId(t.paneId)} · {(t.sizeBytes / 1024).toFixed(1)} KB
              </div>
              {t.workingDirectory && <div className="cmux-list-meta dim mono">{t.workingDirectory}</div>}
            </div>
          ))}
        </div>
        <div className="cmux-split-divider" />
        <div className="cmux-split-content">
          <div className="cmux-transcript-header">
            <div className="cmux-list-title">{selected ? selected.fileName : "Select a capture"}</div>
            <div className="dim mono">
              {selected ? `${new Date(selected.capturedAt).toLocaleString()} · ${selected.workingDirectory ?? "-"}` : ""}
            </div>
          </div>
          <pre className="cmux-transcript mono">{content || "Select a transcript"}</pre>
        </div>
      </div>
      <div className="cmux-panel-footer">
        <button className="cmux-btn" onClick={copyAll} disabled={!content}>Copy All</button>
        <button className="cmux-btn" onClick={openFile} disabled={!selected}>Open File</button>
        <span className="cmux-spacer" />
      </div>
    </div>
  );
}
