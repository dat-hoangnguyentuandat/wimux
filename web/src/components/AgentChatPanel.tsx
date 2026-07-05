import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { api, type AgentMessage, type AgentThread } from "../lib/api";
import { renderMarkdown } from "../lib/markdown";
import { useAppDialog } from "./AppDialog";
import { XIcon } from "./icons";

interface Msg {
  id: string;
  threadId: string;
  role: "user" | "assistant" | "system" | "error";
  content: string;
  createdAtUtc?: string;
  provider?: string;
  model?: string;
  inputTokens?: number;
  outputTokens?: number;
  totalTokens?: number;
}

interface ProviderOption {
  key: string;
  provider: string;
  customName?: string;
  label: string;
  model: string;
}

interface Props {
  workspaceId?: string;
  surfaceId?: string;
  paneId?: string;
  onClose: () => void;
}

const PendingAssistantContent = "__wimux_pending_assistant__";

export function AgentChatPanel({ workspaceId, surfaceId, paneId, onClose }: Props) {
  const dialog = useAppDialog();
  const [messages, setMessages] = useState<Msg[]>([]);
  const [input, setInput] = useState("");
  const [status, setStatus] = useState("Idle");
  const [usage, setUsage] = useState("Usage: -");
  const [context, setContext] = useState("Context: -");
  const [busy, setBusy] = useState(false);
  const [agentSettings, setAgentSettings] = useState<any>(null);
  const [providerMenu, setProviderMenu] = useState(false);
  const [threads, setThreads] = useState<AgentThread[]>([]);
  const [threadSearch, setThreadSearch] = useState("");
  const [messageSearch, setMessageSearch] = useState("");
  const [selectedThreadId, setSelectedThreadId] = useState<string | null>(null);
  const threadRef = useRef<string | undefined>(undefined);
  const bodyRef = useRef<HTMLDivElement>(null);
  const providerWrapRef = useRef<HTMLDivElement>(null);

  const providerOptions = useMemo(() => buildProviderOptions(agentSettings), [agentSettings]);
  const activeProvider = useMemo(() => {
    const active = (agentSettings?.activeProvider ?? "openai").toLowerCase();
    if (active === "custom") {
      const name = agentSettings?.activeCustomProviderName ?? "";
      return providerOptions.find((p) => p.provider === "custom" && p.customName === name) ?? providerOptions.find((p) => p.provider === "custom");
    }
    return providerOptions.find((p) => p.provider === active) ?? providerOptions[0];
  }, [agentSettings, providerOptions]);

  const loadThreadMessages = useCallback(async (threadId: string) => {
    try {
      const msgs = await api.getThreadMessages(threadId);
      setMessages(msgs.map(toMsg));
      updateUsageFromMessages(msgs, setUsage);
    } catch {
      setMessages([]);
      setUsage("Usage: -");
    }
  }, []);

  const refreshThreads = useCallback(async (preferredThreadId?: string, loadPreferred = true) => {
    try {
      let all = await api.getThreads({ workspaceId });
      if (all.length === 0 && workspaceId) {
        all = await api.getThreads();
      }
      setThreads(all);
      const preferred = preferredThreadId || threadRef.current;
      const next = (preferred && all.find((t) => t.id === preferred)) || all[0];
      if (next) {
        const currentThreadId = threadRef.current;
        const shouldLoad = loadPreferred && next.id !== currentThreadId;
        threadRef.current = next.id;
        setSelectedThreadId(next.id);
        if (shouldLoad) await loadThreadMessages(next.id);
      } else if (!next) {
        threadRef.current = undefined;
        setSelectedThreadId(null);
        setMessages([]);
        setUsage("Usage: -");
      }
    } catch { /* ignore */ }
  }, [loadThreadMessages, workspaceId]);

  useEffect(() => {
    api.getAgentSettings().then(setAgentSettings).catch(() => {});
    void refreshThreads();
  }, [refreshThreads]);

  useEffect(() => {
    const reloadAgentSettings = () => {
      api.getAgentSettings().then(setAgentSettings).catch(() => {});
    };
    window.addEventListener("wimux-agent-settings-changed", reloadAgentSettings);
    return () => window.removeEventListener("wimux-agent-settings-changed", reloadAgentSettings);
  }, []);

  useEffect(() => {
    const proto = location.protocol === "https:" ? "wss" : "ws";
    const ws = new WebSocket(`${proto}://${location.host}/ws/agent`);
    ws.onmessage = (e) => {
      try {
        const u = JSON.parse(e.data);
        if (workspaceId && u.workspaceId && u.workspaceId !== workspaceId) return;
        if (surfaceId && u.surfaceId && u.surfaceId !== surfaceId) return;
        const type = updateTypeName(u.type);
        if (type === "ThreadChanged") {
          threadRef.current = u.threadId;
          setSelectedThreadId(u.threadId);
          setThreadSearch("");
          setUsage("Usage: -");
          setStatus("Thread selected");
          void refreshThreads(u.threadId, false);
        } else if (type === "UserMessage") {
          if (threadRef.current !== u.threadId) return;
          appendMessage({ id: u.messageId || crypto.randomUUID(), threadId: u.threadId, role: "user", content: u.message, createdAtUtc: u.createdAtUtc });
          setStatus("User message sent");
        } else if (type === "AssistantDelta") {
          if (threadRef.current !== u.threadId) return;
          setBusy(true);
          setStatus("Streaming response...");
          setMessages((m) => upsertStreamingMessage(m, u.threadId, u.messageId, u.message));
        } else if (type === "AssistantCompleted") {
          if (threadRef.current !== u.threadId) return;
          setBusy(false);
          finalizeAssistant(u);
          setUsage(`Usage: in ${u.inputTokens ?? 0} · out ${u.outputTokens ?? 0} · total ${u.totalTokens ?? 0}`);
          updateContextLabel(u);
          setStatus("Response completed");
          void refreshThreads(u.threadId, false);
        } else if (type === "ContextMetrics") {
          if (threadRef.current && u.threadId && threadRef.current !== u.threadId) return;
          updateContextLabel(u);
        } else if (type === "Status") {
          if (threadRef.current && u.threadId && threadRef.current !== u.threadId) return;
          setStatus(u.message || "Idle");
        } else if (type === "Error") {
          if (threadRef.current !== u.threadId) return;
          setBusy(false);
          appendMessage({ id: u.messageId || crypto.randomUUID(), threadId: u.threadId, role: "error", content: u.message, createdAtUtc: u.createdAtUtc });
          setStatus("Error: " + u.message);
        }
      } catch { /* ignore */ }
    };
    return () => ws.close();
  }, [loadThreadMessages, refreshThreads, surfaceId, workspaceId]);

  useEffect(() => { bodyRef.current?.scrollTo(0, bodyRef.current.scrollHeight); }, [messages, messageSearch]);

  useEffect(() => {
    if (!providerMenu) return;
    const closeOnOutsidePointer = (e: PointerEvent) => {
      const target = e.target as Node | null;
      if (target && providerWrapRef.current?.contains(target)) return;
      setProviderMenu(false);
    };
    const closeOnEscape = (e: KeyboardEvent) => {
      if (e.key === "Escape") setProviderMenu(false);
    };
    document.addEventListener("pointerdown", closeOnOutsidePointer);
    document.addEventListener("keydown", closeOnEscape);
    return () => {
      document.removeEventListener("pointerdown", closeOnOutsidePointer);
      document.removeEventListener("keydown", closeOnEscape);
    };
  }, [providerMenu]);

  const appendMessage = (msg: Msg) => {
    setMessages((m) => {
      if (msg.id && m.some((x) => x.id === msg.id)) return m;
      if (msg.role === "user") {
        const idx = m.findIndex((x) => x.id.startsWith("optimistic-user-") && x.threadId === msg.threadId && x.content === msg.content);
        if (idx >= 0) return [...m.slice(0, idx), msg, ...m.slice(idx + 1)];
      }
      return [...m, msg];
    });
  };

  const finalizeAssistant = (u: any) => {
    setMessages((m) => {
      const id = u.messageId || `stream-${u.threadId}`;
      const next: Msg = {
        id,
        threadId: u.threadId,
        role: "assistant",
        content: u.message,
        createdAtUtc: u.createdAtUtc,
        provider: u.provider,
        model: u.model,
        inputTokens: u.inputTokens,
        outputTokens: u.outputTokens,
        totalTokens: u.totalTokens,
      };
      const idx = m.findIndex((x) => x.id === id || (x.id === `stream-${u.threadId}` && x.role === "assistant"));
      if (idx < 0) return [...m, next];
      return [...m.slice(0, idx), next, ...m.slice(idx + 1)];
    });
  };

  const updateContextLabel = (u: any) => {
    setContext(u.contextBudgetTokens > 0
      ? `Context: ${u.estimatedContextTokens ?? 0}/${u.contextBudgetTokens} tokens${u.contextNeedsCompaction ? " (near limit)" : ""}${u.compactionApplied ? " · compacted" : ""}`
      : "Context: -");
  };

  const newThread = async () => {
    if (!paneId) { setStatus("No active pane selected"); return; }
    try {
      const thread = await api.createThread(paneId);
      threadRef.current = thread.id;
      setSelectedThreadId(thread.id);
      setMessages([]);
      setUsage("Usage: -");
      setStatus("New thread created");
      await refreshThreads(thread.id);
    } catch {
      threadRef.current = undefined;
      setSelectedThreadId(null);
      setMessages([]);
      setUsage("Usage: -");
      setStatus("New thread ready");
    }
  };

  const deleteThread = async () => {
    const id = selectedThreadId || threadRef.current;
    if (!id) { setStatus("Select a thread to delete"); return; }
    const selected = threads.find((t) => t.id === id);
    const ok = await dialog.confirm("Delete thread", `Delete '${selected?.title ?? id}' and all its messages?`, "Delete");
    if (!ok) return;
    try {
      await api.deleteThread(id);
      threadRef.current = undefined;
      setSelectedThreadId(null);
      setMessages([]);
      setUsage("Usage: -");
      await refreshThreads();
      setStatus("Thread deleted");
    } catch { setStatus("Failed to delete thread"); }
  };

  const deleteMessage = async (msg: Msg) => {
    if (!msg.threadId || !msg.id || msg.id.startsWith("stream-")) return;
    try {
      await api.deleteThreadMessage(msg.threadId, msg.id);
      setMessages((m) => m.filter((x) => x.id !== msg.id));
      await refreshThreads(msg.threadId);
      setStatus("Message deleted");
    } catch {
      setStatus("Failed to delete message");
    }
  };

  const selectThread = async (thread: AgentThread) => {
    setSelectedThreadId(thread.id);
    threadRef.current = thread.id;
    setStatus("Thread selected");
    api.activateThread(thread.id, { workspaceId, surfaceId, paneId }).catch(() => {});
    await loadThreadMessages(thread.id);
  };

  const send = async () => {
    const prompt = input.trim();
    if (!prompt) return;
    if (prompt === "/clear") { setMessages([]); setInput(""); setStatus("Messages cleared"); return; }
    if (prompt === "/new-thread") { setInput(""); await newThread(); return; }
    if (prompt === "/context") { setInput(""); setStatus(context); return; }
    if (prompt === "/help") { setInput(""); setStatus("Commands: /clear /new-thread /context /help"); return; }
    if (!paneId) { setStatus("No active pane selected"); return; }

    setInput("");
    let threadId = threadRef.current;
    if (!threadId) {
      try {
        const thread = await api.createThread(paneId);
        threadId = thread.id;
        threadRef.current = thread.id;
        setSelectedThreadId(thread.id);
        setThreadSearch("");
        setMessages([]);
        setUsage("Usage: -");
        await refreshThreads(thread.id, false);
      } catch {
        setStatus("Failed to create thread");
        return;
      }
    }

    const now = new Date().toISOString();
    appendMessage({ id: `optimistic-user-${threadId}-${Date.now()}`, threadId, role: "user", content: prompt, createdAtUtc: now });
    setMessages((m) => upsertStreamingMessage(m, threadId!, undefined, PendingAssistantContent));
    setBusy(true);
    setStatus("Waiting for response...");

    let r: { ok: boolean; threadId?: string; error?: string };
    try {
      r = await api.sendAgentPrompt(paneId, prompt, threadId);
    } catch {
      setBusy(false);
      setStatus("Failed to send prompt");
      setMessages((m) => m.filter((x) => x.id !== `stream-${threadId}`));
      return;
    }

    if (!r.ok) {
      setBusy(false);
      setStatus(r.error || "Agent did not accept the prompt");
      setMessages((m) => m.filter((x) => x.id !== `stream-${threadId}`));
    } else if (r.threadId && r.threadId !== threadRef.current) {
      threadRef.current = r.threadId;
      setSelectedThreadId(r.threadId);
      setThreadSearch("");
      await refreshThreads(r.threadId, false);
    }
  };

  const chooseProvider = async (option: ProviderOption) => {
    if (!agentSettings) return;
    const next = {
      ...agentSettings,
      activeProvider: option.provider,
      activeCustomProviderName: option.customName ?? agentSettings.activeCustomProviderName,
    };
    setAgentSettings(next);
    setProviderMenu(false);
    try {
      await api.saveAgentSettings(next);
      window.dispatchEvent(new CustomEvent("wimux-agent-settings-changed", { detail: next }));
      setStatus(`Provider: ${option.label}`);
    } catch {
      setStatus("Failed to save provider");
    }
  };

  const toggleProviderMenu = async () => {
    if (providerMenu) {
      setProviderMenu(false);
      return;
    }
    try {
      setAgentSettings(await api.getAgentSettings());
    } catch { /* use current settings */ }
    setProviderMenu(true);
  };

  const attachImage = async () => {
    const picked = await api.chooseFile().catch(() => undefined);
    if (picked?.path) setInput((prev) => appendPath(prev, picked.path));
  };

  const attachFile = async () => {
    const picked = await api.chooseFile().catch(() => undefined);
    if (picked?.path) setInput((prev) => appendPath(prev, picked.path));
  };

  const filteredThreads = threadSearch
    ? threads.filter((t) => `${t.title} ${t.lastMessagePreview} ${t.agentName}`.toLowerCase().includes(threadSearch.toLowerCase()))
    : threads;
  const visibleMessages = messageSearch
    ? messages.filter((m) => messageMatches(m, messageSearch))
    : messages;

  return (
    <div
      className="wimux-panel wimux-agent-chat"
      tabIndex={0}
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          e.preventDefault();
          onClose();
        }
      }}
    >
      <div className="wimux-chat-threadbar">
        <SearchIcon className="wimux-search-icon" />
        <input
          className="wimux-chat-search"
          value={threadSearch}
          onChange={(e) => setThreadSearch(e.target.value)}
          title="Search threads"
        />
        <button className="wimux-icon-btn" onClick={newThread} title="New thread"><PlusIcon /></button>
        <button className="wimux-icon-btn" onClick={deleteThread} title="Delete selected thread"><TrashIcon /></button>
        <button className="wimux-icon-btn" onClick={() => refreshThreads()} title="Refresh threads"><RefreshIcon /></button>
        <button className="wimux-icon-btn" onClick={onClose} title="Close"><XIcon /></button>
      </div>

      <div className="wimux-chat-threads">
        {filteredThreads.map((t) => (
          <button
            key={t.id}
            type="button"
            className={"wimux-thread-item" + (t.id === selectedThreadId ? " active" : "")}
            onClick={() => selectThread(t)}
          >
            <span className="wimux-thread-title">{t.title}</span>
            <span className="wimux-thread-meta dim">{formatThreadMeta(t)}</span>
          </button>
        ))}
      </div>

      <div className="wimux-chat-info">
        <div>{busy ? "Streaming response..." : status}</div>
        <div>{usage}</div>
        <div>{context}</div>
        <div className="wimux-message-search-row">
          <SearchIcon className="wimux-search-icon" />
          <input
            className="wimux-chat-search"
            value={messageSearch}
            onChange={(e) => setMessageSearch(e.target.value)}
            title="Search within selected thread"
          />
        </div>
      </div>

      <div className="wimux-chat-body" ref={bodyRef}>
        {!paneId && <div className="wimux-empty">Focus a pane to chat</div>}
        {visibleMessages.map((m) => (
          <div key={m.id} className={"wimux-chat-msg " + m.role}>
            <div className="wimux-chat-msg-main">
              <div className="wimux-chat-role">{messageHeader(m)}</div>
              {isPendingAssistant(m) ? (
                <div className="wimux-chat-content wimux-chat-pending" aria-label="Waiting for response">
                  <span />
                  <span />
                  <span />
                </div>
              ) : (
                <div className="wimux-chat-content md" dangerouslySetInnerHTML={{ __html: renderMarkdown(m.content) }} />
              )}
              <div className="wimux-chat-meta dim mono">{messageMeta(m)}</div>
            </div>
            <button className="wimux-icon-btn wimux-chat-delete" onClick={() => deleteMessage(m)} title="Delete this message"><TrashIcon /></button>
          </div>
        ))}
      </div>

      <div className="wimux-agent-input-wrap">
        <textarea
          className="wimux-agent-textarea"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); } }}
        />
        <div className="wimux-agent-input-bar">
          <div ref={providerWrapRef} style={{ position: "relative", minWidth: 0 }}>
            <button className="wimux-provider-btn" onClick={toggleProviderMenu}>
              <span className="wimux-provider-dot" />
              <span className="wimux-provider-current">{activeProvider ? `${activeProvider.label} · ${shortModel(activeProvider.model)}` : "Agent"}</span>
              <ChevronDownIcon className="wimux-provider-caret" />
            </button>
            {providerMenu && (
              <div className="wimux-provider-menu">
                {providerOptions.map((p) => (
                  <button key={p.key} className="wimux-provider-item" onClick={() => chooseProvider(p)}>
                    <span className="wimux-provider-dot" />
                    <span className="wimux-provider-name">{p.label}</span>
                    <span className="wimux-provider-model dim mono">{shortModel(p.model)}</span>
                  </button>
                ))}
              </div>
            )}
          </div>
          <div className="wimux-agent-actions">
            <button className="wimux-icon-btn" onClick={attachImage} title="Attach image"><ImageIcon /></button>
            <button className="wimux-icon-btn" onClick={attachFile} title="Attach file"><FileIcon /></button>
            <button className="wimux-agent-send" onClick={send} disabled={!paneId || busy} title="Send (Enter)"><ArrowUpIcon /></button>
          </div>
        </div>
      </div>
    </div>
  );
}

function toMsg(m: AgentMessage): Msg {
  const role = (m.role || "user").toLowerCase();
  return {
    id: m.id,
    threadId: m.threadId,
    role: role === "assistant" || role === "system" || role === "error" ? role : "user",
    content: m.content,
    createdAtUtc: m.createdAtUtc,
    provider: m.provider,
    model: m.model,
    inputTokens: m.inputTokens,
    outputTokens: m.outputTokens,
    totalTokens: m.totalTokens,
  };
}

function upsertStreamingMessage(messages: Msg[], threadId: string, messageId: string | undefined, delta: string): Msg[] {
  const id = messageId || `stream-${threadId}`;
  const idx = messages.findIndex((m) => m.id === id || (m.id === `stream-${threadId}` && m.role === "assistant"));
  if (idx < 0) return [...messages, { id, threadId, role: "assistant", content: delta || "" }];
  const current = messages[idx];
  const content = current.content === PendingAssistantContent ? (delta || "") : current.content + (delta || "");
  return [...messages.slice(0, idx), { ...current, id, content }, ...messages.slice(idx + 1)];
}

function isPendingAssistant(m: Msg) {
  return m.role === "assistant" && m.content === PendingAssistantContent;
}

function updateTypeName(type: unknown) {
  if (typeof type === "string") return type;
  if (typeof type === "number") {
    return [
      "ThreadChanged",
      "UserMessage",
      "AssistantDelta",
      "AssistantCompleted",
      "Status",
      "Error",
      "ContextMetrics",
    ][type] ?? "";
  }
  return "";
}

function buildProviderOptions(settings: any): ProviderOption[] {
  const opts: ProviderOption[] = [
    { key: "openai", provider: "openai", label: "OpenAI", model: settings?.openAi?.model ?? "gpt-4o-mini" },
    { key: "anthropic", provider: "anthropic", label: "Anthropic", model: settings?.anthropic?.model ?? "claude-3-5-sonnet-latest" },
    { key: "gemini", provider: "gemini", label: "Gemini", model: settings?.gemini?.model ?? "gemini-2.0-flash" },
  ];
  for (const cp of settings?.customProviders ?? []) {
    opts.push({ key: `custom:${cp.name}`, provider: "custom", customName: cp.name, label: cp.name || "Custom", model: cp.model ?? "" });
  }
  return opts;
}

function updateUsageFromMessages(messages: AgentMessage[], setUsage: (v: string) => void) {
  const last = [...messages].reverse().find((m) => (m.inputTokens || m.outputTokens || m.totalTokens) > 0);
  setUsage(last ? `Usage: in ${last.inputTokens} · out ${last.outputTokens} · total ${last.totalTokens}` : "Usage: -");
}

function messageMatches(m: Msg, query: string) {
  const q = query.toLowerCase();
  return `${m.role} ${m.content} ${m.provider ?? ""} ${m.model ?? ""}`.toLowerCase().includes(q);
}

function messageHeader(m: Msg) {
  const stamp = m.createdAtUtc ? new Date(m.createdAtUtc).toLocaleString() : new Date().toLocaleString();
  return `${m.role} · ${stamp}`;
}

function messageMeta(m: Msg) {
  const bits: string[] = [];
  if ((m.totalTokens ?? 0) > 0) bits.push(`tok ${m.totalTokens}`);
  if ((m.inputTokens ?? 0) > 0 || (m.outputTokens ?? 0) > 0) bits.push(`in ${m.inputTokens ?? 0} / out ${m.outputTokens ?? 0}`);
  if (m.provider || m.model) bits.push(`${m.provider ?? ""}/${m.model ?? ""}`.replace(/^\/|\/$/g, ""));
  return bits.join(" · ");
}

function formatThreadMeta(t: AgentThread) {
  return `${new Date(t.updatedAtUtc).toLocaleString()} · ${t.messageCount} msg · tok ${t.totalTokens}`;
}

function shortModel(model: string) {
  return model.length > 20 ? model.slice(0, 20) + "..." : model;
}

function appendPath(current: string, path: string) {
  return current.trim() ? `${current}\n[${path}]` : `[${path}]`;
}

function SearchIcon({ className }: { className?: string }) {
  return <svg className={className} width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><circle cx="7" cy="7" r="4.5" stroke="currentColor" /><path d="M10.5 10.5L14 14" stroke="currentColor" strokeLinecap="round" /></svg>;
}

function PlusIcon() {
  return <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M8 3V13M3 8H13" stroke="currentColor" strokeLinecap="round" /></svg>;
}

function TrashIcon() {
  return <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M3 4H13M6 4V3H10V4M5 6V13H11V6" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

function RefreshIcon() {
  return <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M13 5V2.5H10.5M12.6 5A5 5 0 1 0 13 10" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

function ChevronDownIcon({ className }: { className?: string }) {
  return <svg className={className} width="10" height="10" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M4 6L8 10L12 6" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

function ImageIcon() {
  return <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><rect x="2.5" y="3" width="11" height="10" rx="1.5" stroke="currentColor" /><circle cx="6" cy="6.5" r="1.2" fill="currentColor" /><path d="M3 12L6.2 9L8.2 10.7L10.4 8.4L13 11.2" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

function FileIcon() {
  return <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M4 2.5H9.5L12 5V13.5H4V2.5Z" stroke="currentColor" strokeLinejoin="round" /><path d="M9.5 2.5V5H12M6 8H10M6 10.5H10" stroke="currentColor" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}

function ArrowUpIcon() {
  return <svg width="15" height="15" viewBox="0 0 16 16" fill="none" aria-hidden="true"><path d="M8 13V3M4.5 6.5L8 3L11.5 6.5" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" /></svg>;
}
