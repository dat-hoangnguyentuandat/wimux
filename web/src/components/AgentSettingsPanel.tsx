import { useEffect, useState } from "react";
import { api } from "../lib/api";

type Tab = "agent" | "tools" | "memory" | "prompts" | "mcp";

export function AgentSettingsPanel({ onClose }: { onClose: () => void }) {
  const [s, setS] = useState<any>(null);
  const [apiKeys, setApiKeys] = useState<Record<string, string>>({});
  const [tab, setTab] = useState<Tab>("agent");

  useEffect(() => { api.getAgentSettings().then(setS); }, []);
  if (!s) return null;
  const set = (patch: any) => setS({ ...s, ...patch });
  const setOpenAi = (patch: any) => setS({ ...s, openAi: { ...s.openAi, ...patch } });
  const setAnthropic = (patch: any) => setS({ ...s, anthropic: { ...s.anthropic, ...patch } });
  const setGemini = (patch: any) => setS({ ...s, gemini: { ...s.gemini, ...patch } });
  const setExa = (patch: any) => setS({ ...s, exa: { ...s.exa, ...patch } });
  const setKey = (name: string, value: string) => name && setApiKeys((prev) => ({ ...prev, [name]: value }));

  const save = async () => {
    await api.saveAgentSettings(s);
    for (const [name, value] of Object.entries(apiKeys)) {
      if (value.trim()) await api.setAgentSecret(name, value.trim());
    }
    onClose();
  };

  return (
    <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="wimux-popup-panel" onMouseDown={(e) => e.stopPropagation()} style={{ maxWidth: 580, maxHeight: 540 }}>
        <div className="wimux-panel-toolbar">
          <div className="wimux-panel-toolbar-row">
            <span className="wimux-panel-title">AGENT SETTINGS</span>
            <span className="wimux-spacer" />
            <button className="wimux-icon-btn" onClick={onClose}>×</button>
          </div>
        </div>
      <div className="wimux-modal-toolbar">
        <button className={tab === "agent" ? "primary" : ""} onClick={() => setTab("agent")}>Agent</button>
        <button className={tab === "tools" ? "primary" : ""} onClick={() => setTab("tools")}>Tools</button>
        <button className={tab === "memory" ? "primary" : ""} onClick={() => setTab("memory")}>Memory</button>
        <button className={tab === "prompts" ? "primary" : ""} onClick={() => setTab("prompts")}>Custom Tools</button>
        <button className={tab === "mcp" ? "primary" : ""} onClick={() => setTab("mcp")}>MCP Servers</button>
      </div>

      {tab === "agent" && (
        <div className="wimux-settings-grid">
          <label className="wimux-field checkbox"><input type="checkbox" checked={!!s.enabled} onChange={(e) => set({ enabled: e.target.checked })} /><span>Enable agent</span></label>
          <label className="wimux-field checkbox"><input type="checkbox" checked={!!s.enableStreaming} onChange={(e) => set({ enableStreaming: e.target.checked })} /><span>Streaming</span></label>
          <label className="wimux-field"><span>Agent name</span><input value={s.agentName ?? ""} onChange={(e) => set({ agentName: e.target.value })} /></label>
          <label className="wimux-field"><span>Handler token</span><input value={s.handler ?? ""} onChange={(e) => set({ handler: e.target.value })} /></label>
          <label className="wimux-field"><span>Active provider</span>
            <select value={s.activeProvider ?? "openai"} onChange={(e) => set({ activeProvider: e.target.value })}>
              <option value="openai">openai</option><option value="anthropic">anthropic</option><option value="gemini">gemini</option><option value="custom">custom</option>
            </select></label>
          <label className="wimux-field"><span>Model (OpenAI)</span><input value={s.openAi?.model ?? ""} onChange={(e) => setOpenAi({ model: e.target.value })} /></label>
          <label className="wimux-field"><span>Base URL (OpenAI)</span><input value={s.openAi?.baseUrl ?? ""} onChange={(e) => setOpenAi({ baseUrl: e.target.value })} /></label>
          <label className="wimux-field"><span>OpenAI API key</span><input type="password" placeholder="(unchanged)" value={apiKeys[s.openAi?.apiKeySecretName || "agent.openai.apiKey"] ?? ""} onChange={(e) => setKey(s.openAi?.apiKeySecretName || "agent.openai.apiKey", e.target.value)} /></label>
          <label className="wimux-field"><span>Model (Anthropic)</span><input value={s.anthropic?.model ?? ""} onChange={(e) => setAnthropic({ model: e.target.value })} /></label>
          <label className="wimux-field"><span>Base URL (Anthropic)</span><input value={s.anthropic?.baseUrl ?? ""} onChange={(e) => setAnthropic({ baseUrl: e.target.value })} /></label>
          <label className="wimux-field"><span>Anthropic API key</span><input type="password" placeholder="(unchanged)" value={apiKeys[s.anthropic?.apiKeySecretName || "agent.anthropic.apiKey"] ?? ""} onChange={(e) => setKey(s.anthropic?.apiKeySecretName || "agent.anthropic.apiKey", e.target.value)} /></label>
          <label className="wimux-field"><span>Model (Gemini)</span><input value={s.gemini?.model ?? ""} onChange={(e) => setGemini({ model: e.target.value })} /></label>
          <label className="wimux-field"><span>Base URL (Gemini)</span><input value={s.gemini?.baseUrl ?? ""} onChange={(e) => setGemini({ baseUrl: e.target.value })} /></label>
          <label className="wimux-field"><span>Gemini API key</span><input type="password" placeholder="(unchanged)" value={apiKeys[s.gemini?.apiKeySecretName || "agent.gemini.apiKey"] ?? ""} onChange={(e) => setKey(s.gemini?.apiKeySecretName || "agent.gemini.apiKey", e.target.value)} /></label>
        </div>
      )}

      {tab === "tools" && (
        <div className="wimux-settings-grid">
          <label className="wimux-field checkbox"><input type="checkbox" checked={!!s.enableBashTool} onChange={(e) => set({ enableBashTool: e.target.checked })} /><span>Bash tool</span></label>
          <label className="wimux-field checkbox"><input type="checkbox" checked={!!s.enableWebSearchTool} onChange={(e) => set({ enableWebSearchTool: e.target.checked })} /><span>Web search</span></label>
          <label className="wimux-field"><span>Exa base URL</span><input value={s.exa?.baseUrl ?? ""} onChange={(e) => setExa({ baseUrl: e.target.value })} /></label>
          <label className="wimux-field"><span>Exa API key</span><input type="password" placeholder="(unchanged)" value={apiKeys[s.exa?.apiKeySecretName || "agent.exa.apiKey"] ?? ""} onChange={(e) => setKey(s.exa?.apiKeySecretName || "agent.exa.apiKey", e.target.value)} /></label>
          <label className="wimux-field"><span>Bash timeout (seconds)</span><input type="number" min={1} value={s.bashTimeoutSeconds ?? 120} onChange={(e) => set({ bashTimeoutSeconds: Number(e.target.value) })} /></label>
          <label className="wimux-field"><span>Default submit key</span>
            <select value={s.defaultSubmitKey ?? "auto"} onChange={(e) => set({ defaultSubmitKey: e.target.value })}>
              <option value="auto">auto</option>
              <option value="enter">enter</option>
              <option value="linefeed">linefeed</option>
              <option value="crlf">crlf</option>
            </select></label>
        </div>
      )}

      {tab === "memory" && (
        <div className="wimux-settings-grid">
          <label className="wimux-field checkbox"><input type="checkbox" checked={!!s.enableConversationMemory} onChange={(e) => set({ enableConversationMemory: e.target.checked })} /><span>Conversation memory</span></label>
          <label className="wimux-field checkbox"><input type="checkbox" checked={!!s.autoCompactContext} onChange={(e) => set({ autoCompactContext: e.target.checked })} /><span>Auto-compact context</span></label>
          <label className="wimux-field"><span>Max messages</span><input type="number" min={1} value={s.maxContextMessages ?? 60} onChange={(e) => set({ maxContextMessages: Number(e.target.value) })} /></label>
          <label className="wimux-field"><span>Context budget (tokens)</span><input type="number" min={1000} value={s.contextBudgetTokens ?? 24000} onChange={(e) => set({ contextBudgetTokens: Number(e.target.value) })} /></label>
          <label className="wimux-field"><span>Compact threshold (%)</span><input type="number" min={50} max={95} value={s.compactThresholdPercent ?? 85} onChange={(e) => set({ compactThresholdPercent: Number(e.target.value) })} /></label>
          <label className="wimux-field"><span>Keep recent</span><input type="number" min={1} value={s.keepRecentMessagesOnCompaction ?? 20} onChange={(e) => set({ keepRecentMessagesOnCompaction: Number(e.target.value) })} /></label>
        </div>
      )}

      {tab === "prompts" && (
        <div className="wimux-settings-grid">
          <label className="wimux-field full"><span>System prompt</span>
            <textarea rows={6} value={s.systemPrompt ?? ""} onChange={(e) => set({ systemPrompt: e.target.value })} /></label>
          <label className="wimux-field full"><span>Custom tools (JSON array)</span>
            <textarea rows={6} value={JSON.stringify(s.customTools ?? [], null, 2)} onChange={(e) => { try { set({ customTools: JSON.parse(e.target.value) }); } catch { /* ignore */ } }} /></label>
        </div>
      )}

      {tab === "mcp" && (
        <div className="wimux-settings-grid">
          <label className="wimux-field full"><span>MCP servers (JSON)</span>
            <textarea rows={8} value={JSON.stringify(s.mcpServers ?? {}, null, 2)} onChange={(e) => { try { set({ mcpServers: JSON.parse(e.target.value) }); } catch { /* ignore */ } }} /></label>
        </div>
      )}

      <div className="wimux-modal-actions">
        <button onClick={onClose}>Cancel</button>
        <button className="primary" onClick={save}>Save</button>
      </div>
      </div>
    </div>
  );
}
