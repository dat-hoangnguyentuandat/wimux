import { useEffect, useRef, useState } from "react";
import { api, type TerminalTheme } from "../lib/api";
import { useAppDialog } from "./AppDialog";

interface Props {
  themes: TerminalTheme[];
  onClose: () => void;
  onApplied: (settings: any) => void;
}

type Tab = "appearance" | "terminal" | "behavior" | "keyboard" | "agent" | "about";

// Monochrome SVG icons matching wimux2 Segoe MDL2 style
const IconAppearance = () => (<svg width="14" height="14" viewBox="0 0 16 16"><circle cx="8" cy="8" r="6" stroke="currentColor" strokeWidth="1.2" fill="none"/><circle cx="8" cy="8" r="2.5" fill="currentColor" opacity="0.4"/></svg>);
const IconTerminal = () => (<svg width="14" height="14" viewBox="0 0 16 16"><rect x="2" y="3" width="12" height="10" rx="1" stroke="currentColor" strokeWidth="1.2" fill="none"/><text x="5" y="11" fill="currentColor" fontSize="7" fontWeight="bold">{">_"}</text></svg>);
const IconBehavior = () => (<svg width="14" height="14" viewBox="0 0 16 16"><circle cx="8" cy="8" r="2.5" stroke="currentColor" strokeWidth="1.2" fill="none"/><path d="M8 3v2.5M8 10.5v2.5M3 8h2.5m5 0H13" stroke="currentColor" strokeWidth="1.2"/></svg>);
const IconKeyboard = () => (<svg width="14" height="14" viewBox="0 0 16 16"><rect x="1" y="5" width="14" height="7" rx="1" stroke="currentColor" strokeWidth="1.2" fill="none"/><line x1="3" y1="8" x2="6" y2="8" stroke="currentColor" strokeWidth="0.8"/><line x1="10" y1="8" x2="13" y2="8" stroke="currentColor" strokeWidth="0.8"/><line x1="5" y1="10" x2="11" y2="10" stroke="currentColor" strokeWidth="0.8"/></svg>);
const IconAgent = () => (<svg width="14" height="14" viewBox="0 0 16 16"><circle cx="8" cy="5" r="2.5" stroke="currentColor" strokeWidth="1.2" fill="none"/><path d="M3 14c0-2.5 2.5-4.5 5-4.5s5 2 5 4.5" stroke="currentColor" strokeWidth="1.2" fill="none"/></svg>);
const IconAbout = () => (<svg width="14" height="14" viewBox="0 0 16 16"><circle cx="8" cy="8" r="6" stroke="currentColor" strokeWidth="1.2" fill="none"/><text x="8" y="11" textAnchor="middle" fill="currentColor" fontSize="9" fontWeight="bold">i</text></svg>);

const SECTIONS: { id: Tab; label: string; Icon: React.FC }[] = [
  { id: "appearance", label: "Appearance", Icon: IconAppearance },
  { id: "terminal", label: "Terminal", Icon: IconTerminal },
  { id: "behavior", label: "Behavior", Icon: IconBehavior },
  { id: "keyboard", label: "Keyboard", Icon: IconKeyboard },
  { id: "agent", label: "Agent", Icon: IconAgent },
  { id: "about", label: "About", Icon: IconAbout },
];

const SHORTCUTS: [string, string][] = [
  ["Ctrl+N", "New workspace"],
  ["Ctrl+1-8", "Jump to workspace"],
  ["Ctrl+Shift+W", "Close workspace"],
  ["Ctrl+T", "New surface"],
  ["Ctrl+W", "Close surface"],
  ["Ctrl+Tab", "Next surface"],
  ["Ctrl+Shift+Tab", "Previous surface"],
  ["Ctrl+D", "Split right"],
  ["Ctrl+Shift+D", "Split down"],
  ["Ctrl+Alt+Arrow", "Focus pane directionally"],
  ["Ctrl+Shift+Z", "Zoom pane toggle"],
  ["Ctrl+Backspace", "Delete previous word"],
  ["Ctrl+B", "Toggle sidebar"],
  ["Ctrl+I", "Notification panel"],
  ["Ctrl+Shift+F", "Search in terminal"],
  ["Shift+W", "Quick write in focused terminal"],
  ["Ctrl+Shift+P", "Command palette"],
  ["Ctrl+Shift+A", "Toggle agent chat"],
  ["Ctrl+Shift+L", "Open command logs"],
  ["Ctrl+Alt+H", "Open command history"],
  ["Ctrl+Shift+V", "Open session vault"],
  ["Right-click", "Context menu"],
];

// ── Reusable field components ──

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="set-row">
      <span className="set-label">{label}</span>
      <div className="set-ctrl">{children}</div>
    </div>
  );
}

function SliderRow({ label, value, min, max, step, onChange, display }: {
  label: string; value: number; min: number; max: number; step: number; onChange: (v: number) => void; display?: string;
}) {
  return (
    <Row label={label}>
      <div className="set-horiz">
        <input type="range" min={min} max={max} step={step} value={value} onChange={(e) => onChange(Number(e.target.value))} style={{ width: 200 }} />
        <span className="set-dim">{display ?? value}</span>
      </div>
    </Row>
  );
}

function Check({ label, checked, onChange, text }: { label: string; checked: boolean; onChange: (v: boolean) => void; text?: string }) {
  return (
    <Row label={label}>
      <label className="set-check">
        <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
        {text && <span>{text}</span>}
      </label>
    </Row>
  );
}

function Sel({ value, onChange, opts, style }: { value: string; onChange: (v: string) => void; opts: string[]; style?: any }) {
  return (
    <select className="set-input" value={value} onChange={(e) => onChange(e.target.value)} style={style}>
      {opts.map((o) => <option key={o} value={o}>{o}</option>)}
    </select>
  );
}

function Txt({ value, onChange, style, password, type, placeholder }: {
  value: string; onChange: (v: string) => void; style?: any; password?: boolean; type?: string; placeholder?: string;
}) {
  return (
    <input className="set-input" type={password ? "password" : (type || "text")} value={value}
      onChange={(e) => onChange(e.target.value)} style={style} placeholder={placeholder} />
  );
}

function Num({ value, onChange, style, min }: { value: number; onChange: (v: number) => void; style?: any; min?: number }) {
  return <input className="set-input" type="number" min={min} value={value}
    onChange={(e) => onChange(Number(e.target.value))} style={style} />;
}

function Block({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="set-box">
      <h3 className="set-h3">{title}</h3>
      {children}
    </div>
  );
}

// ── Custom provider editor ──

function CustomProvidersEditor({ providers, onChange, secretValues, onSecretChange, onSecretClear, isSecretCleared }: {
  providers: any[];
  onChange: (p: any[]) => void;
  secretValues: Record<string, string>;
  onSecretChange: (name: string, value: string) => void;
  onSecretClear: (name: string) => void;
  isSecretCleared: (name: string) => boolean;
}) {
  const [selected, setSelected] = useState(0);
  const p = providers[selected] || { name: "", kind: "anthropic", baseUrl: "", model: "", apiKeySecretName: "", authScheme: "bearer" };
  const defaultSecretName = p.apiKeySecretName || (p.name ? `agent.custom.${sanitizeSecretSegment(p.name)}.apiKey` : "");

  const update = (patch: any) => {
    const next = [...providers];
    next[selected] = { ...next[selected], ...patch };
    onChange(next);
  };

  const add = () => {
    onChange([...providers, { name: "", kind: "anthropic", baseUrl: "", model: "", apiKeySecretName: "", authScheme: "bearer" }]);
    setSelected(providers.length);
  };

  const remove = () => {
    if (providers.length === 0) return;
    onChange(providers.filter((_, i) => i !== selected));
    setSelected(Math.max(0, selected - 1));
  };

  return (
    <div>
      {providers.length > 0 && (
        <Row label="Providers">
          <select className="set-input" value={selected} onChange={(e) => setSelected(Number(e.target.value))} style={{ width: "100%" }}>
            {providers.map((cp: any, i: number) => <option key={i} value={i}>{cp.name || "(unnamed)"}</option>)}
          </select>
        </Row>
      )}
      <Row label="Name"><Txt value={p.name || ""} onChange={(v) => update({ name: v })} style={{ width: "100%" }} /></Row>
      <Row label="API Kind"><Sel value={p.kind || "anthropic"} onChange={(v) => update({ kind: v })} opts={["anthropic", "openai"]} style={{ width: 200 }} /></Row>
      <Row label="Base URL"><Txt value={p.baseUrl || ""} onChange={(v) => update({ baseUrl: v })} style={{ width: "100%" }} /></Row>
      <Row label="Model"><Txt value={p.model || ""} onChange={(v) => update({ model: v })} style={{ width: "100%" }} /></Row>
      <Row label="Auth Scheme"><Sel value={p.authScheme || "bearer"} onChange={(v) => update({ authScheme: v })} opts={["bearer", "x-api-key"]} style={{ width: 160 }} /></Row>
      <Row label="API Key">
        <div className="set-horiz" style={{ width: "100%" }}>
          <Txt password placeholder={isSecretCleared(defaultSecretName) ? "(cleared on save)" : "(unchanged)"} value={defaultSecretName ? (secretValues[defaultSecretName] ?? "") : ""} onChange={(v) => onSecretChange(defaultSecretName, v)} style={{ flex: 1 }} />
          <button className="set-btn" onClick={() => onSecretClear(defaultSecretName)} disabled={!defaultSecretName}>Clear</button>
        </div>
      </Row>
      <div className="set-horiz" style={{ marginTop: 8 }}>
        <button className="set-btn" onClick={add}>Add</button>
        <button className="set-btn" onClick={remove} disabled={providers.length === 0}>Remove</button>
      </div>
    </div>
  );
}

// ── Custom tools editor ──

function CustomToolsEditor({ tools, onChange }: { tools: any[]; onChange: (t: any[]) => void }) {
  const [selected, setSelected] = useState(0);
  const t = tools[selected] || { enabled: true, name: "", description: "", commandTemplate: "" };

  const update = (patch: any) => {
    const next = [...tools];
    next[selected] = { ...next[selected], ...patch };
    onChange(next);
  };

  const add = () => {
    onChange([...tools, { enabled: true, name: "", description: "", commandTemplate: "" }]);
    setSelected(tools.length);
  };

  const remove = () => {
    if (tools.length === 0) return;
    onChange(tools.filter((_, i) => i !== selected));
    setSelected(Math.max(0, selected - 1));
  };

  return (
    <div>
      {tools.length > 0 && (
        <Row label="Tools">
          <select className="set-input" value={selected} onChange={(e) => setSelected(Number(e.target.value))} style={{ width: "100%" }}>
            {tools.map((ct: any, i: number) => <option key={i} value={i}>{ct.name || "(unnamed)"}</option>)}
          </select>
        </Row>
      )}
      <Row label="Name"><Txt value={t.name || ""} onChange={(v) => update({ name: v })} style={{ width: "100%" }} /></Row>
      <Row label="Description"><Txt value={t.description || ""} onChange={(v) => update({ description: v })} style={{ width: "100%" }} /></Row>
      <Row label="Command Template"><Txt value={t.commandTemplate || ""} onChange={(v) => update({ commandTemplate: v })} style={{ width: "100%" }} /></Row>
      <Row label="Enabled">
        <label className="set-check">
          <input type="checkbox" checked={!!t.enabled} onChange={(e) => update({ enabled: e.target.checked })} />
        </label>
      </Row>
      <div className="set-horiz" style={{ marginTop: 8 }}>
        <button className="set-btn" onClick={add}>Add</button>
        <button className="set-btn" onClick={remove} disabled={tools.length === 0}>Remove</button>
      </div>
    </div>
  );
}

// ── MCP servers editor ──

function McpServersEditor({ servers, onChange }: { servers: any[]; onChange: (s: any[]) => void }) {
  const [selected, setSelected] = useState(0);
  const s = servers[selected] || { enabled: true, name: "", command: "", arguments: "", workingDirectory: "" };

  const update = (patch: any) => {
    const next = [...servers];
    next[selected] = { ...next[selected], ...patch };
    onChange(next);
  };

  const add = () => {
    onChange([...servers, { enabled: true, name: "", command: "", arguments: "", workingDirectory: "" }]);
    setSelected(servers.length);
  };

  const remove = () => {
    if (servers.length === 0) return;
    onChange(servers.filter((_, i) => i !== selected));
    setSelected(Math.max(0, selected - 1));
  };

  return (
    <div>
      {servers.length > 0 && (
        <Row label="Servers">
          <select className="set-input" value={selected} onChange={(e) => setSelected(Number(e.target.value))} style={{ width: "100%" }}>
            {servers.map((ms: any, i: number) => <option key={i} value={i}>{ms.name || "(unnamed)"}</option>)}
          </select>
        </Row>
      )}
      <Row label="Name"><Txt value={s.name || ""} onChange={(v) => update({ name: v })} style={{ width: "100%" }} /></Row>
      <Row label="Command"><Txt value={s.command || ""} onChange={(v) => update({ command: v })} style={{ width: "100%" }} /></Row>
      <Row label="Arguments"><Txt value={s.arguments || ""} onChange={(v) => update({ arguments: v })} style={{ width: "100%" }} /></Row>
      <Row label="Working Dir"><Txt value={s.workingDirectory || ""} onChange={(v) => update({ workingDirectory: v })} style={{ width: "100%" }} /></Row>
      <Row label="Enabled">
        <label className="set-check">
          <input type="checkbox" checked={!!s.enabled} onChange={(e) => update({ enabled: e.target.checked })} />
        </label>
      </Row>
      <div className="set-horiz" style={{ marginTop: 8 }}>
        <button className="set-btn" onClick={add}>Add</button>
        <button className="set-btn" onClick={remove} disabled={servers.length === 0}>Remove</button>
      </div>
    </div>
  );
}

// ── Main component ──

export function SettingsModal({ themes, onClose, onApplied }: Props) {
  const dialog = useAppDialog();
  const [settings, setSettings] = useState<any>(null);
  const [agentSecretValues, setAgentSecretValues] = useState<Record<string, string>>({});
  const [agentSecretsToClear, setAgentSecretsToClear] = useState<Set<string>>(new Set());
  const [customToolsJson, setCustomToolsJson] = useState("[]");
  const [mcpServersJson, setMcpServersJson] = useState("[]");
  const [submitProfilesJson, setSubmitProfilesJson] = useState("[]");
  const [shells, setShells] = useState<{ name: string; path: string }[]>([]);
  const [tab, setTab] = useState<Tab>("appearance");
  const fileRef = useRef<HTMLInputElement>(null);

  function syncAgentJsonDrafts(agent: any) {
    setCustomToolsJson(JSON.stringify(agent.customTools || [], null, 2));
    setMcpServersJson(JSON.stringify(agent.mcpServers || [], null, 2));
    setSubmitProfilesJson(JSON.stringify(agent.submitProfiles || [], null, 2));
  }

  useEffect(() => {
    api.getSettings().then((saved) => {
      setSettings(saved);
      syncAgentJsonDrafts(saved.agent || {});
    });
    api.getShells().then(setShells).catch(() => setShells([]));
  }, []);

  if (!settings) return null;

  const s = settings;
  const set = (patch: any) => setSettings({ ...settings, ...patch });
  const theme = themes.find((t) => t.name === s.themeName);
  const ag = s.agent || {};
  const setAgent = (patch: any) => set({ agent: { ...ag, ...patch } });
  const setAgentSecretValue = (name: string, value: string) => {
    if (!name) return;
    setAgentSecretValues((prev) => ({ ...prev, [name]: value }));
    setAgentSecretsToClear((prev) => {
      const next = new Set(prev);
      next.delete(name);
      return next;
    });
  };
  const getAgentSecretValue = (name: string) => name ? agentSecretValues[name] ?? "" : "";
  const clearAgentSecret = (name: string) => {
    if (!name) return;
    setAgentSecretValues((prev) => ({ ...prev, [name]: "" }));
    setAgentSecretsToClear((prev) => new Set(prev).add(name));
  };
  const isAgentSecretCleared = (name: string) => !!name && agentSecretsToClear.has(name);
  const renderSecretInput = (name: string) => (
    <div className="set-horiz" style={{ width: "100%" }}>
      <Txt
        password
        placeholder={isAgentSecretCleared(name) ? "(cleared on save)" : "(unchanged)"}
        value={getAgentSecretValue(name)}
        onChange={(v) => setAgentSecretValue(name, v)}
        style={{ flex: 1 }}
      />
      <button className="set-btn" onClick={() => clearAgentSecret(name)}>Clear</button>
    </div>
  );

  const save = async () => {
    const draft = buildSettingsWithJsonDrafts(settings, {
      customToolsJson,
      mcpServersJson,
      submitProfilesJson,
    });
    if (draft.error) {
      await dialog.alert("Settings", draft.error);
      return;
    }
    const normalized = normalizeAgentSettingsForSave(draft.settings);
    const validationError = validateAgentSettings(normalized.agent || {});
    if (validationError) {
      await dialog.alert("Settings", validationError);
      return;
    }
    const pendingSecrets = Object.entries(agentSecretValues).filter(([, value]) => value.trim());
    const updated = await api.saveSettings(normalized);
    for (const name of agentSecretsToClear) {
      await api.clearAgentSecret(name);
    }
    for (const [name, value] of pendingSecrets) {
      await api.setAgentSecret(name, value.trim());
    }
    window.dispatchEvent(new CustomEvent("wimux-agent-settings-changed", { detail: updated.agent }));
    onApplied(updated);
    onClose();
  };

  const reset = async () => {
    const ok = await dialog.confirm("Reset settings", "Reset all settings to defaults?", "Reset");
    if (!ok) return;
    api.saveSettings({}).then((saved) => {
      setSettings(saved);
      syncAgentJsonDrafts(saved.agent || {});
      window.dispatchEvent(new CustomEvent("wimux-agent-settings-changed", { detail: saved.agent }));
      onApplied(saved);
    });
  };

  const importSettings = () => fileRef.current?.click();
  const onImport = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    file.text().then((t) => {
      try {
        const imported = { ...settings, ...JSON.parse(t) };
        setSettings(imported);
        syncAgentJsonDrafts(imported.agent || {});
      } catch { /* */ }
    });
    e.target.value = "";
  };

  const exportSettings = () => {
    const blob = new Blob([JSON.stringify(settings, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url; a.download = "wimux-settings.json"; a.click();
    URL.revokeObjectURL(url);
  };

  const renderPane = () => {
    switch (tab) {
      // ─── Appearance ────────────────────────────────────────
      case "appearance":
        return (
          <div className="set-scroll">
            <h2 className="set-h2">Appearance</h2>
            <Row label="Font Family">
              <Sel value={s.fontFamily} onChange={(v) => set({ fontFamily: v })} style={{ width: 240 }}
                opts={["Cascadia Code", "Consolas", "Fira Code", "JetBrains Mono", "Source Code Pro", "MesloLGS NF", "monospace"]} />
            </Row>
            <SliderRow label="Font Size" value={s.fontSize} min={9} max={28} step={1} onChange={(v) => set({ fontSize: v })} display={`${s.fontSize}px`} />
            <Row label="Theme">
              <div className="set-horiz">
                <Sel value={s.themeName} onChange={(v) => set({ themeName: v })} style={{ width: 200 }}
                  opts={themes.map((t) => t.name)} />
                {theme && <span className="set-swatch" style={{ background: theme.background }}><span style={{ color: theme.foreground }}>Aa</span></span>}
              </div>
            </Row>
            <Row label="App Theme">
              <Sel value={s.uiThemeName} onChange={(v) => set({ uiThemeName: v })} style={{ width: 160 }} opts={["Dark+", "Light", "High Contrast"]} />
            </Row>
            <Row label="System Theme">
              <span style={{ fontSize: 13 }}>{window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "Dark" : "Light"} <span className="set-dim">(detected)</span></span>
            </Row>
            <SliderRow label="Opacity" value={s.opacity ?? 1} min={0.5} max={1} step={0.05} onChange={(v) => set({ opacity: v })} display={`${Math.round((s.opacity ?? 1) * 100)}%`} />
            <Row label="Cursor Style">
              <Sel value={s.cursorStyle} onChange={(v) => set({ cursorStyle: v })} style={{ width: 140 }} opts={["bar", "block", "underline"]} />
            </Row>
            <Check label="Cursor Blink" checked={!!s.cursorBlink} onChange={(v) => set({ cursorBlink: v })} text="Enable blinking cursor" />
            <Row label="Blink Rate (ms)"><Num value={s.cursorBlinkMs ?? 530} onChange={(v) => set({ cursorBlinkMs: v })} style={{ width: 80 }} min={100} /></Row>
            <Row label="Line Height"><Num value={s.lineHeight ?? 1} onChange={(v) => set({ lineHeight: v })} style={{ width: 80 }} min={0.5} /></Row>
            <Row label="Padding (px)"><Num value={s.padding ?? 0} onChange={(v) => set({ padding: v })} style={{ width: 80 }} min={0} /></Row>
          </div>
        );

      // ─── Terminal ──────────────────────────────────────────
      case "terminal":
        return (
          <div className="set-scroll">
            <h2 className="set-h2">Terminal</h2>
            <Row label="Color Preset">
              <Sel value={s.themeName} onChange={(v) => set({ themeName: v })} style={{ width: 240 }} opts={themes.map((t) => t.name)} />
            </Row>
            <Check label="Custom Colors" checked={!!s.useCustomTerminalColors} onChange={(v) => set({ useCustomTerminalColors: v })} text="Override preset colors" />
            {s.useCustomTerminalColors && (
              <>
                {(["Background", "Foreground", "Cursor", "Selection"] as const).map((name) => {
                  const key = "customTerminal" + name as keyof typeof s;
                  const val = (s[key] || "").slice(0, 7);
                  return (
                    <Row key={name} label={name}>
                      <div className="set-horiz">
                        <span className="set-swatch-mini" style={{ background: val || "#000" }} />
                        <Txt value={val} onChange={(v) => set({ [key]: v })} style={{ width: 110 }} />
                        <input type="color" value={val} onChange={(e) => set({ [key]: e.target.value })} style={{ width: 28, height: 28, border: 0, cursor: "pointer" }} />
                      </div>
                    </Row>
                  );
                })}
                <Row label="">
                  <button className="set-btn" onClick={() => set({
                    customTerminalBackground: "", customTerminalForeground: "",
                    customTerminalCursor: "", customTerminalSelection: ""
                  })}>Reset Colors</button>
                </Row>
              </>
            )}
            <Row label="Default Shell">
              <select className="set-input" value={s.defaultShell ?? ""} onChange={(e) => set({ defaultShell: e.target.value })} style={{ width: 240 }}>
                <option value="">(auto)</option>
                {shells.map((sh) => <option key={sh.path} value={sh.path}>{sh.name}</option>)}
              </select>
            </Row>
            <Row label="Shell Arguments"><Txt value={s.defaultShellArgs ?? ""} onChange={(v) => set({ defaultShellArgs: v })} style={{ width: "100%" }} /></Row>
            <Row label="Scrollback Lines"><Num value={s.scrollbackLines} onChange={(v) => set({ scrollbackLines: v })} style={{ width: 100 }} min={0} /></Row>
            <Check label="Bell Sound" checked={!!s.bellSound} onChange={(v) => set({ bellSound: v })} />
            <Check label="Visual Bell" checked={!!s.visualBell} onChange={(v) => set({ visualBell: v })} />
            <Check label="Bracketed Paste" checked={!!s.bracketedPaste} onChange={(v) => set({ bracketedPaste: v })} />
            <Check label="Vim Mode" checked={!!s.vimMode} onChange={(v) => set({ vimMode: v })} text="Enable vim keybindings (hjkl, w/b/e, d/c/y, dd/cc)" />
            <Row label="Word Separators"><Txt value={s.wordSeparators ?? " \t\n{}[]()\"'`,:;<>"} onChange={(v) => set({ wordSeparators: v })} style={{ width: "100%" }} /></Row>
          </div>
        );

      // ─── Behavior ──────────────────────────────────────────
      case "behavior":
        return (
          <div className="set-scroll">
            <h2 className="set-h2">Behavior</h2>
            <Check label="Restore Session" checked={!!s.restoreSessionOnStartup} onChange={(v) => set({ restoreSessionOnStartup: v })} text="Restore previous session on startup" />
            <Check label="Confirm Close" checked={!!s.confirmOnClose} onChange={(v) => set({ confirmOnClose: v })} text="Ask before closing window" />
            <Check label="Auto Copy" checked={!!s.autoCopyOnSelect} onChange={(v) => set({ autoCopyOnSelect: v })} text="Copy to clipboard on text selection" />
            <Check label="Ctrl+Click URLs" checked={!!s.ctrlClickOpensUrls} onChange={(v) => set({ ctrlClickOpensUrls: v })} text="Open URLs with Ctrl+Click" />
            <Check label="Quick Write" checked={s.quickWriteEnabled !== false} onChange={(v) => set({ quickWriteEnabled: v })} text="Show the floating write button after left-clicking a terminal pane" />
            <Check label="Right-Click Menu" checked={!!s.rightClickAlwaysMenu} onChange={(v) => set({ rightClickAlwaysMenu: v })} text="Always open context menu on right-click, even inside TUI apps (overrides their paste). Off = Shift+Right-click opens the menu inside TUI apps." />
            <Row label="Auto Save (sec)"><Num value={s.autoSaveIntervalSeconds} onChange={(v) => set({ autoSaveIntervalSeconds: v })} style={{ width: 80 }} min={5} /></Row>
            <Row label="Log Retention (days)"><Num value={s.commandLogRetentionDays} onChange={(v) => set({ commandLogRetentionDays: v })} style={{ width: 80 }} min={0} /></Row>
            <Check label="Capture On Close" checked={!!s.captureTranscriptsOnClose} onChange={(v) => set({ captureTranscriptsOnClose: v })} text="Save transcript when pane/surface/workspace/app closes" />
            <Check label="Capture On Clear" checked={!!s.captureTranscriptsOnClear} onChange={(v) => set({ captureTranscriptsOnClear: v })} text="Save transcript before Clear Terminal" />
            <Row label="Capture Retention"><Num value={s.transcriptRetentionDays} onChange={(v) => set({ transcriptRetentionDays: v })} style={{ width: 80 }} min={0} /></Row>
          </div>
        );

      // ─── Keyboard ──────────────────────────────────────────
      case "keyboard":
        return (
          <div className="set-scroll">
            <h2 className="set-h2">Keyboard Shortcuts</h2>
            <div className="set-box">
              {SHORTCUTS.map(([key, desc]) => (
                <div key={key} className="kb-row">
                  <span className="kb-key">{key}</span>
                  <span>{desc}</span>
                </div>
              ))}
            </div>
          </div>
        );

      // ─── Agent ─────────────────────────────────────────────
      case "agent":
        return (
          <div className="set-scroll">
            <h2 className="set-h2">Agent</h2>
            <Check label="Enable Agent" checked={!!ag.enabled} onChange={(v) => setAgent({ enabled: v })} text="Enable pane handler commands" />
            <Row label="Agent Name"><Txt value={ag.agentName ?? ""} onChange={(v) => setAgent({ agentName: v })} style={{ width: 220 }} /></Row>
            <Row label="Primary Handler"><Txt value={ag.handler ?? "/agent"} onChange={(v) => setAgent({ handler: v })} style={{ width: 220 }} /></Row>
            <Row label="Extra Handlers"><Txt value={ag.additionalHandlers ?? ""} onChange={(v) => setAgent({ additionalHandlers: v })} style={{ width: "100%" }} placeholder="Comma/space separated" /></Row>
            <Row label="Active Provider">
              <Sel value={ag.activeProvider ?? "openai"} onChange={(v) => setAgent({ activeProvider: v })} style={{ width: 220 }}
                opts={["openai", "anthropic", "gemini", "custom"]} />
            </Row>
            {ag.activeProvider === "custom" && (
              <Row label="Custom Provider">
                <Sel value={ag.activeCustomProviderName ?? ""} onChange={(v) => setAgent({ activeCustomProviderName: v })} style={{ width: 220 }}
                  opts={(ag.customProviders || []).map((cp: any) => cp.name).filter(Boolean)} />
              </Row>
            )}

            <div className="set-box">
              <h3 className="set-h3">System Prompt</h3>
              <textarea className="set-input set-area" rows={4} value={ag.systemPrompt ?? ""} onChange={(e) => setAgent({ systemPrompt: e.target.value })} />
            </div>

            {/* OpenAI */}
            <Block title="OpenAI-Compatible">
              <Row label="Base URL"><Txt value={ag.openAi?.baseUrl ?? ""} onChange={(v) => setAgent({ openAi: { ...ag.openAi, baseUrl: v } })} style={{ width: "100%" }} /></Row>
              <Row label="Model"><Txt value={ag.openAi?.model ?? ""} onChange={(v) => setAgent({ openAi: { ...ag.openAi, model: v } })} style={{ width: "100%" }} /></Row>
              <Row label="API Key">{renderSecretInput(ag.openAi?.apiKeySecretName ?? "agent.openai.apiKey")}</Row>
            </Block>

            {/* Anthropic */}
            <Block title="Anthropic-Compatible">
              <Row label="Base URL"><Txt value={ag.anthropic?.baseUrl ?? ""} onChange={(v) => setAgent({ anthropic: { ...ag.anthropic, baseUrl: v } })} style={{ width: "100%" }} /></Row>
              <Row label="Model"><Txt value={ag.anthropic?.model ?? ""} onChange={(v) => setAgent({ anthropic: { ...ag.anthropic, model: v } })} style={{ width: "100%" }} /></Row>
              <Row label="API Key">{renderSecretInput(ag.anthropic?.apiKeySecretName ?? "agent.anthropic.apiKey")}</Row>
            </Block>

            {/* Gemini */}
            <Block title="Gemini (Google AI)">
              <Row label="Base URL"><Txt value={ag.gemini?.baseUrl ?? ""} onChange={(v) => setAgent({ gemini: { ...ag.gemini, baseUrl: v } })} style={{ width: "100%" }} /></Row>
              <Row label="Model"><Txt value={ag.gemini?.model ?? ""} onChange={(v) => setAgent({ gemini: { ...ag.gemini, model: v } })} style={{ width: "100%" }} /></Row>
              <Row label="API Key">{renderSecretInput(ag.gemini?.apiKeySecretName ?? "agent.gemini.apiKey")}</Row>
            </Block>

            {/* Custom Providers */}
            <Block title="Custom Providers">
              <CustomProvidersEditor
                providers={ag.customProviders || []}
                onChange={(v) => setAgent({ customProviders: v })}
                secretValues={agentSecretValues}
                onSecretChange={setAgentSecretValue}
                onSecretClear={clearAgentSecret}
                isSecretCleared={isAgentSecretCleared} />
            </Block>

            {/* Tools */}
            <Block title="Tools">
              <Check label="Bash Tool" checked={!!ag.enableBashTool} onChange={(v) => setAgent({ enableBashTool: v })} />
              <Row label="Bash Timeout (s)"><Num value={ag.bashTimeoutSeconds ?? 120} onChange={(v) => setAgent({ bashTimeoutSeconds: v })} style={{ width: 80 }} min={1} /></Row>
              <Check label="Web Search" checked={!!ag.enableWebSearchTool} onChange={(v) => setAgent({ enableWebSearchTool: v })} />
              <Row label="Exa Base URL"><Txt value={ag.exa?.baseUrl ?? ""} onChange={(v) => setAgent({ exa: { ...ag.exa, baseUrl: v } })} style={{ width: "100%" }} /></Row>
              <Row label="Exa API Key">{renderSecretInput(ag.exa?.apiKeySecretName ?? "agent.exa.apiKey")}</Row>
            </Block>

            {/* Custom Tools */}
            <Block title="Custom Tools">
              <Row label="Mode">
                <Sel value={ag.useJsonForCustomTools ? "JSON" : "Creator"} onChange={(v) => {
                  if (v === "JSON") setCustomToolsJson(JSON.stringify(ag.customTools || [], null, 2));
                  setAgent({ useJsonForCustomTools: v === "JSON" });
                }} style={{ width: 160 }}
                  opts={["Creator", "JSON"]} />
              </Row>
              {ag.useJsonForCustomTools ? (
                <textarea className="set-input set-area" rows={6} style={{ width: "100%" }}
                  value={customToolsJson}
                  onChange={(e) => setCustomToolsJson(e.target.value)} />
              ) : (
                <CustomToolsEditor tools={ag.customTools || []} onChange={(v) => { setAgent({ customTools: v }); setCustomToolsJson(JSON.stringify(v || [], null, 2)); }} />
              )}
            </Block>

            {/* MCP Servers */}
            <Block title="MCP Servers">
              <Row label="Mode">
                <Sel value={ag.useJsonForMcpServers ? "JSON" : "Creator"} onChange={(v) => {
                  if (v === "JSON") setMcpServersJson(JSON.stringify(ag.mcpServers || [], null, 2));
                  setAgent({ useJsonForMcpServers: v === "JSON" });
                }} style={{ width: 160 }}
                  opts={["Creator", "JSON"]} />
              </Row>
              {ag.useJsonForMcpServers ? (
                <textarea className="set-input set-area" rows={6} style={{ width: "100%" }}
                  value={mcpServersJson}
                  onChange={(e) => setMcpServersJson(e.target.value)} />
              ) : (
                <McpServersEditor servers={ag.mcpServers || []} onChange={(v) => { setAgent({ mcpServers: v }); setMcpServersJson(JSON.stringify(v || [], null, 2)); }} />
              )}
            </Block>

            {/* Pane Submit */}
            <Block title="Pane Submit">
              <Row label="Default Submit Key">
                <Sel value={ag.defaultSubmitKey ?? "auto"} onChange={(v) => setAgent({ defaultSubmitKey: v })} style={{ width: 140 }}
                  opts={["auto", "enter", "linefeed", "crlf"]} />
              </Row>
              <Check label="Enable Auto Fallback" checked={!!ag.enableSubmitFallback} onChange={(v) => setAgent({ enableSubmitFallback: v })} />
              <Row label="Fallback Wait (ms)"><Num value={ag.submitFallbackWaitMs ?? 350} onChange={(v) => setAgent({ submitFallbackWaitMs: v })} style={{ width: 100 }} min={0} /></Row>
              <Row label="Fallback Order"><Txt value={ag.submitFallbackOrder ?? "enter,linefeed"} onChange={(v) => setAgent({ submitFallbackOrder: v })} style={{ width: "100%" }}
                placeholder="enter,linefeed,crlf" /></Row>
              <Check label="Enable Submit Profiles" checked={!!ag.enableTargetSubmitProfiles} onChange={(v) => setAgent({ enableTargetSubmitProfiles: v })} />
              <div style={{ marginTop: 6 }}>
                <textarea className="set-input set-area" rows={6} style={{ width: "100%" }}
                  value={submitProfilesJson}
                  onChange={(e) => setSubmitProfilesJson(e.target.value)} />
              </div>
            </Block>

            {/* Agent Files & Skills */}
            <Block title="Agent Files &amp; Skills">
              <Check label="Auto Discover" checked={!!ag.autoDiscoverAgentFiles} onChange={(v) => setAgent({ autoDiscoverAgentFiles: v })} />
              <Row label="Instructions Path"><Txt value={ag.agentInstructionsPath ?? ""} onChange={(v) => setAgent({ agentInstructionsPath: v })} style={{ width: "100%" }} /></Row>
              <Row label="Skills Root Path"><Txt value={ag.skillsRootPath ?? ""} onChange={(v) => setAgent({ skillsRootPath: v })} style={{ width: "100%" }} /></Row>
            </Block>

            {/* Chat Panel */}
            <Block title="Chat Panel">
              <Row label="Chat Font Family">
                <Sel value={ag.chatFontFamily ?? "Cascadia Code"} onChange={(v) => setAgent({ chatFontFamily: v })} style={{ width: 240 }}
                  opts={["Cascadia Code", "Consolas", "Fira Code", "JetBrains Mono", "Source Code Pro", "monospace"]} />
              </Row>
              <Row label="Chat Font Size"><Num value={ag.chatFontSize ?? 13} onChange={(v) => setAgent({ chatFontSize: v })} style={{ width: 80 }} min={9} /></Row>
            </Block>

            {/* Conversation Memory */}
            <Block title="Conversation Memory">
              <Check label="Enable Memory" checked={!!ag.enableConversationMemory} onChange={(v) => setAgent({ enableConversationMemory: v })} />
              <Check label="Enable Streaming" checked={!!ag.enableStreaming} onChange={(v) => setAgent({ enableStreaming: v })} />
              <Check label="Auto Compact Context" checked={!!ag.autoCompactContext} onChange={(v) => setAgent({ autoCompactContext: v })} />
              <Row label="Max Context Messages"><Num value={ag.maxContextMessages ?? 60} onChange={(v) => setAgent({ maxContextMessages: v })} style={{ width: 80 }} min={1} /></Row>
              <Row label="Context Budget Tokens"><Num value={ag.contextBudgetTokens ?? 24000} onChange={(v) => setAgent({ contextBudgetTokens: v })} style={{ width: 100 }} min={100} /></Row>
              <Row label="Compact Threshold %"><Num value={ag.compactThresholdPercent ?? 85} onChange={(v) => setAgent({ compactThresholdPercent: v })} style={{ width: 80 }} min={1} /></Row>
              <Row label="Keep Recent on Compact"><Num value={ag.keepRecentMessagesOnCompaction ?? 20} onChange={(v) => setAgent({ keepRecentMessagesOnCompaction: v })} style={{ width: 80 }} min={1} /></Row>
            </Block>
          </div>
        );

      // ─── About ─────────────────────────────────────────────
      case "about":
        return (
          <div className="set-scroll">
            <h2 className="set-h2">About</h2>
            <div className="set-box">
              <h3 style={{ fontSize: 18, fontWeight: "bold", margin: "0 0 4px" }}>wimux</h3>
              <p className="set-dim" style={{ margin: "0 0 12px", fontSize: 13, lineHeight: 1.5 }}>
                A modern terminal multiplexer designed for AI coding agents. Split panes, workspaces, command palette, and intelligent terminal management.
              </p>
              <div className="set-sep" />
              <p className="mono set-dim" style={{ fontSize: 12, lineHeight: 1.8, margin: "12px 0 0" }}>
                Runtime: .NET 10<br />
                Frontend: React 19 + dockview<br />
                Config: %LOCALAPPDATA%/wimux/settings.json
              </p>
            </div>
          </div>
        );

      default:
        return null;
    }
  };

  return (
    <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="wimux-settings-popup" onMouseDown={(e) => e.stopPropagation()}>
        <div className="set-titlebar">
          <span>Settings</span>
          <button className="wimux-icon-btn" onClick={onClose}>×</button>
        </div>
        <div className="set-body">
          <div className="set-nav">
            {SECTIONS.map((sec) => (
              <button key={sec.id} className={tab === sec.id ? "active" : ""} onClick={() => setTab(sec.id)}>
                <sec.Icon />
                <span>{sec.label}</span>
              </button>
            ))}
          </div>
          <div className="set-div" />
          <div className="set-content">{renderPane()}</div>
        </div>
        <div className="set-bar">
          <button className="set-btn" onClick={reset}>Reset to Defaults</button>
          <span className="wimux-spacer" />
          <button className="set-btn" onClick={exportSettings}>Export</button>
          <button className="set-btn" onClick={importSettings}>Import</button>
          <input ref={fileRef} type="file" accept="application/json" style={{ display: "none" }} onChange={onImport} />
          <button className="set-btn" onClick={onClose}>Cancel</button>
          <button className="set-btn primary" onClick={save}>Save</button>
        </div>
      </div>
    </div>
  );
}

function buildSettingsWithJsonDrafts(settings: any, drafts: { customToolsJson: string; mcpServersJson: string; submitProfilesJson: string }) {
  const agent = settings?.agent;
  if (!agent) return { settings };

  let nextAgent = { ...agent };
  if (agent.useJsonForCustomTools) {
    const parsed = parseJsonArrayDraft(drafts.customToolsJson, "custom tools");
    if (parsed.error) return { error: parsed.error };
    nextAgent = { ...nextAgent, customTools: parsed.value };
  }

  if (agent.useJsonForMcpServers) {
    const parsed = parseJsonArrayDraft(drafts.mcpServersJson, "MCP servers");
    if (parsed.error) return { error: parsed.error };
    nextAgent = { ...nextAgent, mcpServers: parsed.value };
  }

  const parsedProfiles = parseJsonArrayDraft(drafts.submitProfilesJson, "submit profiles");
  if (parsedProfiles.error) return { error: parsedProfiles.error };
  nextAgent = { ...nextAgent, submitProfiles: parsedProfiles.value };

  return { settings: { ...settings, agent: nextAgent } };
}

function parseJsonArrayDraft(text: string, label: string): { value?: any[]; error?: string } {
  if (!text.trim()) return { value: [] };
  try {
    const value = JSON.parse(text);
    if (!Array.isArray(value)) return { error: `${capitalize(label)} JSON must be an array.` };
    return { value };
  } catch (err: any) {
    return { error: `Invalid ${label} JSON: ${err?.message || "parse error"}` };
  }
}

function capitalize(value: string) {
  return value ? value[0].toUpperCase() + value.slice(1) : value;
}

function normalizeAgentSettingsForSave(settings: any) {
  const agent = settings?.agent;
  if (!agent) return settings;

  const customProviders = (agent.customProviders ?? []).filter((provider: any) => {
    return (provider.name ?? "").trim()
      || (provider.baseUrl ?? "").trim()
      || (provider.model ?? "").trim()
      || (provider.apiKeySecretName ?? "").trim();
  }).map((provider: any) => {
    const name = (provider.name ?? "").trim();
    return {
      ...provider,
      name,
      baseUrl: (provider.baseUrl ?? "").trim(),
      model: (provider.model ?? "").trim(),
      kind: (provider.kind || "anthropic").toLowerCase(),
      authScheme: (provider.authScheme || "bearer").toLowerCase(),
      apiKeySecretName: provider.apiKeySecretName || (name ? `agent.custom.${sanitizeSecretSegment(name)}.apiKey` : ""),
      anthropicVersion: provider.anthropicVersion || "2023-06-01",
    };
  });

  let activeCustomProviderName = agent.activeCustomProviderName ?? "";
  if ((agent.activeProvider ?? "").toLowerCase() === "custom" && !activeCustomProviderName && customProviders.length > 0) {
    activeCustomProviderName = customProviders[0].name;
  }
  const activeProvider = (agent.activeProvider || "openai").toLowerCase();
  const normalizedActiveProvider = ["openai", "anthropic", "gemini", "custom"].includes(activeProvider) ? activeProvider : "openai";

  return {
    ...settings,
    agent: {
      ...agent,
      agentName: (agent.agentName ?? "").trim() || "assistant",
      handler: (agent.handler ?? "").trim() || "/agent",
      additionalHandlers: (agent.additionalHandlers ?? "").trim(),
      systemPrompt: (agent.systemPrompt ?? "").trim(),
      activeProvider: normalizedActiveProvider,
      activeCustomProviderName,
      openAi: {
        ...agent.openAi,
        baseUrl: (agent.openAi?.baseUrl ?? "").trim(),
        model: (agent.openAi?.model ?? "").trim(),
        apiKeySecretName: agent.openAi?.apiKeySecretName || "agent.openai.apiKey",
      },
      anthropic: {
        ...agent.anthropic,
        baseUrl: (agent.anthropic?.baseUrl ?? "").trim(),
        model: (agent.anthropic?.model ?? "").trim(),
        apiKeySecretName: agent.anthropic?.apiKeySecretName || "agent.anthropic.apiKey",
      },
      gemini: {
        ...agent.gemini,
        baseUrl: (agent.gemini?.baseUrl ?? "").trim(),
        model: (agent.gemini?.model ?? "").trim(),
        apiKeySecretName: agent.gemini?.apiKeySecretName || "agent.gemini.apiKey",
      },
      exa: {
        ...agent.exa,
        baseUrl: (agent.exa?.baseUrl ?? "").trim(),
        apiKeySecretName: agent.exa?.apiKeySecretName || "agent.exa.apiKey",
      },
      customProviders,
      bashTimeoutSeconds: clampNumber(agent.bashTimeoutSeconds, 1, 1800, 120),
      defaultSubmitKey: ["auto", "enter", "linefeed", "crlf"].includes((agent.defaultSubmitKey || "").toLowerCase())
        ? (agent.defaultSubmitKey || "auto").toLowerCase()
        : "auto",
      submitFallbackWaitMs: clampNumber(agent.submitFallbackWaitMs, 0, 5000, 350),
      submitFallbackOrder: (agent.submitFallbackOrder ?? "").trim() || "enter,linefeed",
      chatFontFamily: (agent.chatFontFamily ?? "").trim() || settings.fontFamily,
      chatFontSize: clampNumber(agent.chatFontSize, 9, 28, 13),
      maxContextMessages: clampNumber(agent.maxContextMessages, 8, 500, 60),
      contextBudgetTokens: clampNumber(agent.contextBudgetTokens, 2048, 1000000, 24000),
      compactThresholdPercent: clampNumber(agent.compactThresholdPercent, 50, 95, 85),
      keepRecentMessagesOnCompaction: clampNumber(agent.keepRecentMessagesOnCompaction, 4, 400, 20),
      agentInstructionsPath: (agent.agentInstructionsPath ?? "").trim(),
      skillsRootPath: (agent.skillsRootPath ?? "").trim(),
    },
  };
}

function validateAgentSettings(agent: any) {
  const customProviders = Array.isArray(agent.customProviders) ? agent.customProviders : [];
  const providerNames = new Set<string>();
  for (const provider of customProviders) {
    const name = (provider.name ?? "").trim();
    if (!name) return "Custom provider name is required. Fill in 'Name' or remove the provider.";
    if (!providerNames.add(name.toLowerCase())) return `Duplicate custom provider name: '${name}'.`;
    if (!(provider.baseUrl ?? "").trim()) return `Custom provider '${name}' is missing 'Base URL'.`;
    const kind = (provider.kind || "anthropic").toLowerCase();
    if (kind !== "anthropic" && kind !== "openai") return `Custom provider '${name}' has unsupported API kind '${provider.kind}'.`;
    const authScheme = (provider.authScheme || "bearer").toLowerCase();
    if (authScheme !== "bearer" && authScheme !== "x-api-key") return `Custom provider '${name}' has unsupported auth scheme '${provider.authScheme}'.`;
  }

  const toolsError = validateNamedArray(agent.customTools, "Custom tool", "commandTemplate");
  if (toolsError) return toolsError;

  const serversError = validateNamedArray(agent.mcpServers, "MCP server", "command");
  if (serversError) return serversError;

  const profiles = Array.isArray(agent.submitProfiles) ? agent.submitProfiles : [];
  const profileNames = new Set<string>();
  for (let i = 0; i < profiles.length; i++) {
    const profile = profiles[i];
    const row = i + 1;
    const name = (profile.name ?? "").trim();
    if (!name) return `Submit profile at index ${row} is missing 'name'.`;
    if (!profileNames.add(name.toLowerCase())) return `Duplicate submit profile name: '${name}'.`;
    const order = (profile.submitOrder ?? "").trim();
    if (!order) return `Submit profile '${name}' is missing 'submitOrder'.`;
    for (const token of order.split(/[,;\s]+/).filter(Boolean)) {
      const key = token.toLowerCase();
      if (!["enter", "linefeed", "crlf", "lf", "cr", "ctrl+j", "ctrl+m"].includes(key)) {
        return `Submit profile '${name}' has unsupported submit key '${token}'.`;
      }
    }
    profile.repeatCount = clampNumber(profile.repeatCount, 1, 8, 1);
    profile.delayMs = clampNumber(profile.delayMs, 0, 3000, 0);
    profile.waitMs = Number(profile.waitMs) < 0 ? -1 : clampNumber(profile.waitMs, 0, 5000, 0);
  }

  return "";
}

function validateNamedArray(value: any, label: string, requiredField: string) {
  if (!Array.isArray(value)) return "";
  const names = new Set<string>();
  for (let i = 0; i < value.length; i++) {
    const item = value[i];
    const row = i + 1;
    const name = (item.name ?? "").trim();
    if (!name) return `${label} at index ${row} is missing 'name'.`;
    if (!names.add(name.toLowerCase())) return `Duplicate ${label.toLowerCase()} name: '${name}'.`;
    if (!(item[requiredField] ?? "").trim()) return `${label} '${name}' is missing '${requiredField}'.`;
  }
  return "";
}

function clampNumber(value: any, min: number, max: number, fallback: number) {
  const n = Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(min, Math.min(max, n));
}

function sanitizeSecretSegment(name: string) {
  const value = name.trim().toLowerCase().replace(/[^a-z0-9_-]+/g, "_");
  return value || "default";
}
