import { useEffect, useRef, useState } from "react";
import { api, type ExternalAgent } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import { ArrowUpIcon, RefreshIcon, SearchIcon } from "./icons";

interface Props {
  onClose: () => void;
  onSend?: (paneId: string, text: string) => void;
}

function statusColor(s: number) {
  if (s === 1) return "var(--success)";
  if (s === 2) return "var(--warning)";
  return "var(--text-dim)";
}

function shortId(id: string) {
  if (!id) return "-";
  return id.length <= 8 ? id : id.slice(0, 8);
}

export function AgentsPanel({ onClose, onSend }: Props) {
  const [agents, setAgents] = useState<ExternalAgent[]>([]);
  const [selected, setSelected] = useState<ExternalAgent | null>(null);
  const [convo, setConvo] = useState<{ role: string; content: string; timestamp: string }[]>([]);
  const [query, setQuery] = useState("");
  const [input, setInput] = useState("");
  const bodyRef = useRef<HTMLDivElement>(null);

  const load = () => api.getAgents().then(setAgents).catch(() => setAgents([]));
  useEffect(() => {
    load();
    const t = setInterval(load, 6000);
    return () => clearInterval(t);
  }, []);
  useEffect(() => {
    if (selected?.sessionFilePath) api.getAgentConversation(selected.sessionFilePath).then(setConvo).catch(() => setConvo([]));
    else setConvo([]);
  }, [selected]);
  useEffect(() => { bodyRef.current?.scrollTo(0, bodyRef.current.scrollHeight); }, [convo]);

  const filtered = agents.filter((a) => {
    if (!query) return true;
    const q = query.toLowerCase();
    return a.name.toLowerCase().includes(q) || a.typeLabel.toLowerCase().includes(q) || (a.projectPath ?? "").toLowerCase().includes(q);
  });

  const counts = agents.reduce((acc, a) => {
    if (a.status === 1) acc.run++;
    else if (a.status === 2) acc.wait++;
    else acc.idle++;
    return acc;
  }, { run: 0, wait: 0, idle: 0 });

  const send = () => {
    const text = input.trim();
    if (!text || !selected) return;
    if (onSend) onSend(shortId(selected.pid?.toString() ?? ""), text + "\r");
    setInput("");
  };

  return (
    <div className="cmux-panel">
      <div className="cmux-panel-toolbar">
        <div className="cmux-panel-toolbar-row">
          <span className="cmux-panel-title">AGENTS</span>
          <span className="cmux-badge">{agents.length}</span>
          <SearchIcon className="cmux-search-icon" />
          <input style={{ flex: 1 }} value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter by name, type, project..." />
          <button className="cmux-icon-btn" onClick={load} title="Refresh (F5)"><RefreshIcon /></button>
          <span className="cmux-spacer" />
        </div>
      </div>
      <div className="cmux-panel-body cmux-split-body cmux-wide-split-body">
        <div className="cmux-list">
          {filtered.length === 0 && <div className="cmux-empty">No agents detected</div>}
          {filtered.map((a) => (
            <div
              key={a.pid + a.sessionId}
              className={"cmux-list-item" + (selected?.sessionId === a.sessionId ? " active" : "")}
              onClick={() => setSelected(a)}
            >
              <div className="cmux-list-row">
                <span className="cmux-status-dot" style={{ background: statusColor(a.status) }} />
                <span className="cmux-list-title">{a.name}</span>
                <span className="cmux-list-tag">{a.typeLabel}</span>
              </div>
              <div className="cmux-list-meta dim mono">
                {a.statusLabel} · pid {a.pid} · {a.projectPath || a.summary}
              </div>
            </div>
          ))}
        </div>
        <div className="cmux-split-divider" />
        <div className="cmux-split-content cmux-agent-preview">
          <div className="cmux-preview-header">
            <span className="cmux-panel-title">PREVIEW</span>
            <span className="cmux-list-title">{selected?.name ?? "Select an agent"}</span>
          </div>
          <div className="cmux-agent-messages" ref={bodyRef}>
            {convo.length === 0 && <div className="cmux-empty">Select an agent to preview its conversation</div>}
            {convo.map((m, i) => (
              <div key={i} className={"cmux-agent-msg " + m.role}>
                <div className="cmux-agent-role dim">{m.role}</div>
                <div className="cmux-agent-content md" dangerouslySetInnerHTML={{ __html: renderMarkdown(m.content) }} />
              </div>
            ))}
          </div>
          <div className="cmux-chat-input">
            <textarea
              placeholder="Send a follow-up to this agent's session…"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } }}
            />
            <button className="cmux-send-btn" onClick={send} disabled={!selected || !input.trim()} title="Send (Enter)"><ArrowUpIcon /></button>
          </div>
        </div>
      </div>
      <div className="cmux-panel-footer">
        <span className="dim">{counts.run} run · {counts.wait} wait · {counts.idle} idle</span>
        <span className="cmux-spacer" />
        <span className="dim">↑/↓ nav · F5 refresh · Esc close</span>
      </div>
    </div>
  );
}
