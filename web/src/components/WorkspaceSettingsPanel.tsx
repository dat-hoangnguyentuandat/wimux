import { useEffect, useState } from "react";
import { api, type SshProfile, type Workspace } from "../lib/api";
import { PlusIcon, TrashIcon } from "./icons";

interface Props {
  workspace: Workspace;
  onClose: () => void;
}

type Tab = "env" | "ssh";

export function WorkspaceSettingsPanel({ workspace, onClose }: Props) {
  const [tab, setTab] = useState<Tab>("env");
  const [envText, setEnvText] = useState("");
  const [ssh, setSsh] = useState<SshProfile[]>([]);

  useEffect(() => {
    api.getWorkspaceEnv(workspace.id).then((env) => {
      setEnvText(Object.entries(env).map(([k, v]) => `${k}=${v}`).join("\n"));
    });
    api.getWorkspaceSsh(workspace.id).then(setSsh);
  }, [workspace.id]);

  const saveEnv = async () => {
    const env: Record<string, string> = {};
    for (const line of envText.split("\n")) {
      const i = line.indexOf("=");
      if (i > 0) env[line.slice(0, i).trim()] = line.slice(i + 1).trim();
    }
    await api.setWorkspaceEnv(workspace.id, env);
    onClose();
  };
  const saveSsh = async () => { await api.setWorkspaceSsh(workspace.id, ssh); onClose(); };

  const addProfile = () =>
    setSsh([...ssh, { id: crypto.randomUUID(), name: "New Profile", host: "hostname", port: 22, user: "user" }]);
  const updateProfile = (id: string, patch: Partial<SshProfile>) =>
    setSsh(ssh.map((p) => (p.id === id ? { ...p, ...patch } : p)));
  const removeProfile = (id: string) => setSsh(ssh.filter((p) => p.id !== id));

  const buildCommand = (p: SshProfile) => {
    const parts = ["ssh"];
    if (p.port && p.port !== 22) parts.push("-p", String(p.port));
    if (p.identityFile) parts.push("-i", p.identityFile);
    parts.push(`${p.user ? p.user + "@" : ""}${p.host}`);
    return parts.join(" ");
  };

  return (
    <div className="cmux-panel cmux-workspace-settings">
      <div className="cmux-panel-toolbar">
        <div className="cmux-panel-toolbar-row">
          <span className="cmux-panel-title">SSH / ENV</span>
          <span className="cmux-list-title">{workspace.name}</span>
          <span className="cmux-spacer" />
        </div>
      </div>
      <div className="cmux-modal-toolbar">
        <button className={tab === "env" ? "primary" : ""} onClick={() => setTab("env")}>Environment</button>
        <button className={tab === "ssh" ? "primary" : ""} onClick={() => setTab("ssh")}>SSH Profiles</button>
      </div>
      {tab === "env" && (
        <div className="cmux-panel-body">
          <p className="dim">One <code>KEY=value</code> per line. Injected into every new terminal in this workspace.</p>
          <textarea className="mono" rows={12} style={{ width: "100%", minHeight: 240 }}
            value={envText} onChange={(e) => setEnvText(e.target.value)} />
          <div className="cmux-modal-actions"><button onClick={onClose}>Cancel</button><button className="primary" onClick={saveEnv}>Save</button></div>
        </div>
      )}
      {tab === "ssh" && (
        <div className="cmux-panel-body">
          <div className="cmux-ssh-list">
            {ssh.map((p) => (
              <div className="cmux-ssh-card" key={p.id}>
                <label className="cmux-field"><span>Name</span><input value={p.name} onChange={(e) => updateProfile(p.id, { name: e.target.value })} /></label>
                <label className="cmux-field"><span>User</span><input value={p.user} onChange={(e) => updateProfile(p.id, { user: e.target.value })} /></label>
                <label className="cmux-field"><span>Host</span><input value={p.host} onChange={(e) => updateProfile(p.id, { host: e.target.value })} /></label>
                <label className="cmux-field"><span>Port</span><input type="number" value={p.port} onChange={(e) => updateProfile(p.id, { port: Number(e.target.value) })} /></label>
                <label className="cmux-field cmux-ssh-identity"><span>Identity file</span><input value={p.identityFile ?? ""} onChange={(e) => updateProfile(p.id, { identityFile: e.target.value })} /></label>
                <div className="cmux-ssh-preview mono dim">{buildCommand(p)}</div>
                <button className="cmux-icon-btn cmux-ssh-remove" onClick={() => removeProfile(p.id)} title="Remove profile"><TrashIcon /></button>
              </div>
            ))}
          </div>
          <div className="cmux-modal-actions">
            <button onClick={addProfile}><PlusIcon /> Add profile</button>
            <span className="cmux-spacer" />
            <button onClick={onClose}>Cancel</button>
            <button className="primary" onClick={saveSsh}>Save</button>
          </div>
        </div>
      )}
    </div>
  );
}
