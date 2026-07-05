import { useEffect, useRef, useState } from "react";
import { api, type ExternalAgent } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import { ArrowUpIcon, FileIcon, ImageIcon, RefreshIcon, SearchIcon, XIcon } from "./icons";

interface Props {
  onClose: () => void;
}

function statusColor(s: number) {
  if (s === 1) return "var(--success)";
  if (s === 2) return "var(--warning)";
  return "var(--text-dim)";
}

function sameAgent(left: ExternalAgent, right: ExternalAgent) {
  if (left.sessionFilePath && right.sessionFilePath) {
    return left.sessionFilePath.toLowerCase() === right.sessionFilePath.toLowerCase();
  }
  if (left.projectPath && right.projectPath) {
    return left.type === right.type && left.projectPath.toLowerCase() === right.projectPath.toLowerCase();
  }
  return left.pid === right.pid;
}

function agentKey(agent: ExternalAgent) {
  return agent.sessionFilePath?.toLowerCase()
    || `${agent.type}:${agent.projectPath?.toLowerCase() || ""}:${agent.pid}`;
}

export function AgentsPanel({ onClose }: Props) {
  const [agents, setAgents] = useState<ExternalAgent[]>([]);
  const [selected, setSelected] = useState<ExternalAgent | null>(null);
  const [selectedKeys, setSelectedKeys] = useState<Set<string>>(new Set());
  const [convo, setConvo] = useState<{ role: string; content: string; timestamp: string }[]>([]);
  const [query, setQuery] = useState("");
  const [input, setInput] = useState("");
  const [status, setStatus] = useState("Idle");
  const bodyRef = useRef<HTMLDivElement>(null);
  const pendingSendsRef = useRef<Record<string, string[]>>({});

  const load = () => api.getAgents()
    .then((items) => {
      setAgents(items);
      setSelected((current) => {
        if (current) {
          const match = items.find((a) => sameAgent(a, current));
          if (match) return match;
        }
        return items[0] ?? null;
      });
      setSelectedKeys((current) => {
        const available = new Set(items.map(agentKey));
        const next = new Set([...current].filter((key) => available.has(key)));
        if (next.size === 0 && items[0]) next.add(agentKey(items[0]));
        return next;
      });
    })
    .catch(() => {
      setAgents([]);
      setSelected(null);
      setSelectedKeys(new Set());
    });
  useEffect(() => {
    load();
    const t = setInterval(load, 6000);
    return () => clearInterval(t);
  }, []);
  useEffect(() => {
    if (selected?.sessionFilePath) {
      const key = agentKey(selected);
      api.getAgentConversation(selected.sessionFilePath)
        .then((items) => {
          const merged = [...items];
          const remaining: string[] = [];
          for (const pending of pendingSendsRef.current[key] || []) {
            const duplicate = merged.some((m) => m.role.toLowerCase() === "user" && m.content.trim() === pending.trim());
            if (duplicate) continue;
            remaining.push(pending);
            merged.push({ role: "user", content: pending, timestamp: new Date().toISOString() });
          }
          pendingSendsRef.current[key] = remaining;
          setConvo(merged);
        })
        .catch(() => setConvo([]));
    }
    else {
      setConvo([]);
    }
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

  const send = async (overrideText?: string) => {
    const text = (overrideText ?? input).trim();
    if (!text || !selected) return;
    const targets = agents.filter((a) => selectedKeys.has(agentKey(a)));
    const sendTargets = targets.length > 0 ? targets : [selected];
    if (overrideText == null) setInput("");
    let sent = 0;
    let lastError = "";
    try {
      for (const target of sendTargets) {
        const r = await api.sendExternalAgentMessage(target, text);
        if (r.ok) sent++;
        else lastError = r.error || "Agent did not accept message";
      }
      if (sendTargets.length === 1) {
        const key = agentKey(selected);
        pendingSendsRef.current[key] = [...(pendingSendsRef.current[key] || []), text];
      }
      const previewText = sendTargets.length > 1 ? `[broadcast to ${sendTargets.length} agents] ${text}` : text;
      setConvo((items) => [...items, { role: "user", content: previewText, timestamp: new Date().toISOString() }]);
      if (sendTargets.length > 1) setStatus(`Broadcast to ${sendTargets.length} agents (${sent} sent)`);
      else if (sent > 0) setStatus(`Sent to ${selected.name}`);
      else setStatus(`Queued for ${selected.name}${lastError ? `; ${lastError}` : ""}`);
    } catch (err: any) {
      setStatus(err?.message || "Failed to send message");
    }
  };

  const appendAttachment = (path: string) => {
    setInput((current) => current.trim() ? `${current}\n${path}` : path);
  };

  const attachImage = async () => {
    const picked = await api.chooseFile(selected?.projectPath).catch(() => undefined);
    if (picked?.path) appendAttachment(picked.path);
  };

  const attachFile = async () => {
    const picked = await api.chooseFile(selected?.projectPath).catch(() => undefined);
    if (picked?.path) appendAttachment(picked.path);
  };

  const editMessage = (content: string) => {
    setInput(content);
    setStatus("Editing prior prompt");
  };

  const selectRelative = (delta: number) => {
    if (filtered.length === 0) return;
    const currentIndex = selected ? filtered.findIndex((a) => sameAgent(a, selected)) : -1;
    const nextIndex = Math.max(0, Math.min(filtered.length - 1, currentIndex + delta));
    const next = filtered[nextIndex];
    setSelected(next);
    setSelectedKeys(new Set([agentKey(next)]));
  };

  const selectAgent = (agent: ExternalAgent, extend: boolean) => {
    setSelected(agent);
    setSelectedKeys((current) => {
      if (!extend) return new Set([agentKey(agent)]);
      const next = new Set(current);
      const key = agentKey(agent);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      if (next.size === 0) next.add(key);
      return next;
    });
  };

  return (
    <div
      className="wimux-panel"
      tabIndex={0}
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          e.preventDefault();
          if (e.target instanceof HTMLTextAreaElement || e.target instanceof HTMLInputElement) {
            (e.target as HTMLElement).blur();
          } else {
            onClose();
          }
        } else if (e.key === "F5") { e.preventDefault(); load(); }
        else if (e.key === "ArrowDown" && !(e.target instanceof HTMLTextAreaElement) && !(e.target instanceof HTMLInputElement)) {
          e.preventDefault(); selectRelative(1);
        } else if (e.key === "ArrowUp" && !(e.target instanceof HTMLTextAreaElement) && !(e.target instanceof HTMLInputElement)) {
          e.preventDefault(); selectRelative(-1);
        }
      }}
    >
      <div className="wimux-panel-toolbar">
        <div className="wimux-panel-toolbar-row">
          <span className="wimux-panel-title">AGENTS</span>
          <span className="wimux-badge">{agents.length}</span>
          <SearchIcon className="wimux-search-icon" />
          <input style={{ flex: 1 }} value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Filter by name, type, project..." />
          <button className="wimux-icon-btn" onClick={load} title="Refresh (F5)"><RefreshIcon /></button>
          <span className="wimux-spacer" />
          <button className="wimux-icon-btn" onClick={onClose} title="Close (Esc)"><XIcon /></button>
        </div>
      </div>
      <div className="wimux-panel-body wimux-split-body wimux-wide-split-body">
        <div className="wimux-list">
          {filtered.length === 0 && <div className="wimux-empty">No agents detected</div>}
          {filtered.map((a) => (
            <div
              key={a.pid + a.sessionId}
              className={"wimux-list-item" + (selectedKeys.has(agentKey(a)) ? " active" : "")}
              onClick={(e) => selectAgent(a, e.ctrlKey || e.metaKey || e.shiftKey)}
            >
              <div className="wimux-list-row">
                <span className="wimux-status-dot" style={{ background: statusColor(a.status) }} />
                <span className="wimux-list-title">{a.name}</span>
                <span className="wimux-list-tag">{a.typeLabel}</span>
              </div>
              <div className="wimux-list-meta dim mono">
                {a.statusLabel} · pid {a.pid} · {a.projectPath || a.summary}
              </div>
            </div>
          ))}
        </div>
        <div className="wimux-split-divider" />
        <div className="wimux-split-content wimux-agent-preview">
          <div className="wimux-preview-header">
            <span className="wimux-panel-title">PREVIEW</span>
            <span className="wimux-list-title">{selectedKeys.size > 1 ? `${selectedKeys.size} agents selected` : (selected?.name ?? "Select an agent")}</span>
          </div>
          <div className="wimux-agent-messages" ref={bodyRef}>
            {convo.length === 0 && <div className="wimux-empty">Select an agent to preview its conversation</div>}
            {convo.map((m, i) => (
              <div key={i} className={"wimux-agent-msg " + m.role}>
                <div className="wimux-agent-msg-head">
                  <div className="wimux-agent-role dim">{m.role}</div>
                  {m.role.toLowerCase() === "user" && (
                    <div className="wimux-agent-msg-actions">
                      <button className="wimux-icon-btn" onClick={() => editMessage(m.content)} title="Edit and resend">Edit</button>
                      <button className="wimux-icon-btn" onClick={() => send(m.content)} title="Resend this prompt"><RefreshIcon /></button>
                    </div>
                  )}
                </div>
                <div className="wimux-agent-content md" dangerouslySetInnerHTML={{ __html: renderMarkdown(m.content) }} />
              </div>
            ))}
          </div>
          <div className="wimux-agent-input-wrap">
            <textarea
              className="wimux-agent-textarea"
              placeholder="Send a follow-up to this agent's session…"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } }}
            />
            <div className="wimux-agent-input-bar">
              <div className="wimux-provider-btn" title={selected?.summary || selected?.projectPath || selected?.name || "Agent"}>
                <span className="wimux-provider-dot" />
                <span className="wimux-provider-current">
                  {selectedKeys.size > 1 ? `${selectedKeys.size} agents` : (selected ? `${selected.name} · ${selected.typeLabel}` : "Agent")}
                </span>
              </div>
              <div className="wimux-agent-actions">
                <button className="wimux-icon-btn" onClick={attachImage} title="Attach image"><ImageIcon /></button>
                <button className="wimux-icon-btn" onClick={attachFile} title="Attach file"><FileIcon /></button>
                <button className="wimux-agent-send" onClick={() => send()} disabled={!selected || !input.trim()} title="Send (Enter)"><ArrowUpIcon /></button>
              </div>
            </div>
          </div>
        </div>
      </div>
      <div className="wimux-panel-footer">
        <span className="dim">{counts.run} run · {counts.wait} wait · {counts.idle} idle</span>
        <span className="wimux-spacer" />
        <span className="dim">{status}</span>
      </div>
    </div>
  );
}
