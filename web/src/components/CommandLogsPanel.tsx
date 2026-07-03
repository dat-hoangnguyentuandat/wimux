import { useEffect, useMemo, useState } from "react";
import { api, type CommandLogEntry, type Workspace } from "../lib/api";

interface Props {
  workspace?: Workspace;
  onClose: () => void;
  onInsert?: (text: string) => void;
  onOpenVault?: () => void;
}

function shortId(id: string) {
  if (!id) return "-";
  return id.length <= 8 ? id : id.slice(0, 8);
}

function fmtTime(iso: string) {
  return new Date(iso).toLocaleTimeString();
}

export function CommandLogsPanel({ workspace, onClose, onInsert, onOpenVault }: Props) {
  const [dates, setDates] = useState<string[]>([]);
  const [date, setDate] = useState("");
  const [query, setQuery] = useState("");
  const [entries, setEntries] = useState<CommandLogEntry[]>([]);

  useEffect(() => {
    api.getLogDates().then((d) => {
      setDates(d);
      setDate(d[0] ?? new Date().toISOString().slice(0, 10));
    });
  }, []);

  useEffect(() => {
    if (query.trim()) api.getLogs({ q: query }).then(setEntries);
    else if (date) api.getLogs({ date }).then(setEntries);
  }, [date, query]);

  const workspaceNameById = useMemo(() => {
    const map: Record<string, string> = {};
    if (workspace) map[workspace.id] = workspace.name;
    return map;
  }, [workspace]);

  const fmtEntity = (id: string, name: string) =>
    name && name !== shortId(id) ? `${name} · ${shortId(id)}` : shortId(id);

  const insert = (e: CommandLogEntry) => {
    if (e.command && onInsert) onInsert(e.command);
  };
  const run = (e: CommandLogEntry) => {
    if (e.command && onInsert) {
      onInsert(e.command + "\r");
      onClose();
    }
  };
  const copy = (e: CommandLogEntry) => {
    if (e.command) navigator.clipboard?.writeText(e.command).catch(() => {});
  };

  return (
    <div className="cmux-panel cmux-wide-data-panel cmux-command-logs-panel">
      <div className="cmux-panel-toolbar">
        <div className="cmux-panel-toolbar-row">
          <label>Date</label>
          <select value={date} onChange={(e) => { setQuery(""); setDate(e.target.value); }}>
            {dates.length === 0 && <option value={date}>{date}</option>}
            {dates.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
          <button className="cmux-btn" onClick={() => { setQuery(""); api.getLogDates().then((d) => { setDates(d); setDate(d[0] ?? date); }); }}>Refresh</button>
          <button className="cmux-btn" onClick={onOpenVault}>Session Vault</button>
          <span className="cmux-spacer" />
        </div>
        <div className="cmux-panel-toolbar-row">
          <label>Search</label>
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Command or path..." />
          <button className="cmux-btn" onClick={() => { setQuery(""); }}>Clear</button>
        </div>
      </div>
      <div className="cmux-panel-body cmux-list-body cmux-wide-data-body cmux-command-logs-body">
        <table className="cmux-grid">
          <thead>
            <tr>
              <th style={{ width: 78 }}>Time</th>
              <th style={{ width: 210 }}>Workspace</th>
              <th style={{ width: 180 }}>Surface</th>
              <th style={{ width: 90 }}>Pane</th>
              <th>Command</th>
              <th style={{ width: 220 }}>Working Dir</th>
              <th style={{ width: 50 }}>Exit</th>
              <th style={{ width: 90 }}>Duration</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((e) => (
              <tr key={e.id}
                onDoubleClick={() => run(e)}
                onKeyDown={(ev) => {
                  if (ev.key === "Enter") { ev.preventDefault(); ev.shiftKey ? insert(e) : run(e); }
                  else if (ev.key === "c" && (ev.ctrlKey || ev.metaKey)) { ev.preventDefault(); copy(e); }
                }}
                tabIndex={0}>
                <td className="mono">{fmtTime(e.startedAt)}</td>
                <td>{fmtEntity(e.workspaceId, workspaceNameById[e.workspaceId] ?? "")}</td>
                <td>{shortId(e.surfaceId)}</td>
                <td className="mono">{shortId(e.paneId)}</td>
                <td className="mono cmux-cmd-cell">{e.command}</td>
                <td className="mono dim">{e.workingDirectory ?? "-"}</td>
                <td className={e.exitCode === 0 ? "ok" : e.exitCode ? "err" : "dim"}>{e.exitCode == null ? "-" : e.exitCode}</td>
                <td className="dim">{e.durationDisplay}</td>
              </tr>
            ))}
            {entries.length === 0 && <tr><td colSpan={8} className="cmux-empty">No commands</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="cmux-panel-footer">
        <button className="cmux-btn" onClick={() => entries.length && copy(entries[0])}>Copy Command</button>
        <button className="cmux-btn" onClick={() => entries.length && insert(entries[0])}>Insert in Focused Pane</button>
        <button className="cmux-btn primary" onClick={() => entries.length && run(entries[0])}>Run in Focused Pane</button>
        <span className="cmux-spacer" />
        <span className="dim cmux-footer-summary">{entries.length} entries</span>
      </div>
    </div>
  );
}
