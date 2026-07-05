import { useEffect, useState } from "react";
import { api, type QuotaSnapshot } from "../lib/api";

const WINDOW_LABELS: Record<string, string> = {
  Last5Hours: "Last 5 hours",
  Today: "Today",
  Last7Days: "Last 7 days",
  Last30Days: "Last 30 days",
  AllTime: "All time",
};

export function QuotaPanel({ onClose }: { onClose: () => void }) {
  const [snap, setSnap] = useState<QuotaSnapshot | null>(null);
  const [window, setWindow] = useState("Today");

  const load = () => api.getQuota().then(setSnap);
  useEffect(() => { load(); }, []);

  const windows = snap ? Object.keys(snap.windows) : [];
  const data = snap?.windows[window];
  const totals = data?.rows.reduce((acc, r) => ({
    requests: acc.requests + r.requests,
    input: acc.input + r.inputTokens,
    output: acc.output + r.outputTokens,
    total: acc.total + r.totalTokens,
  }), { requests: 0, input: 0, output: 0, total: 0 }) ?? { requests: 0, input: 0, output: 0, total: 0 };

  return (
    <div className="wimux-panel wimux-wide-data-panel wimux-quota-panel">
      <div className="wimux-panel-toolbar">
        <div className="wimux-panel-toolbar-row">
          <label>Window</label>
          <select value={window} onChange={(e) => setWindow(e.target.value)}>
            {windows.map((w) => <option key={w} value={w}>{WINDOW_LABELS[w] ?? w}</option>)}
          </select>
          <button className="wimux-btn" onClick={load}>Refresh</button>
          <span className="wimux-spacer" />
        </div>
      </div>
      <div className="wimux-panel-body wimux-quota-body wimux-wide-data-body">
        <div className="wimux-quota-summary">
          <div><div className="wimux-stat-label">Requests</div><div className="wimux-stat">{totals.requests.toLocaleString()}</div></div>
          <div><div className="wimux-stat-label">Input Tokens</div><div className="wimux-stat">{totals.input.toLocaleString()}</div></div>
          <div><div className="wimux-stat-label">Output Tokens</div><div className="wimux-stat">{totals.output.toLocaleString()}</div></div>
          <div><div className="wimux-stat-label">Total Tokens</div><div className="wimux-stat accent">{totals.total.toLocaleString()}</div></div>
        </div>
        <table className="wimux-grid">
          <thead>
            <tr>
              <th style={{ width: 140 }}>Provider</th>
              <th>Model</th>
              <th style={{ width: 90 }}>Requests</th>
              <th style={{ width: 100 }}>Input</th>
              <th style={{ width: 100 }}>Output</th>
              <th style={{ width: 100 }}>Total</th>
              <th style={{ width: 160 }}>Last activity</th>
            </tr>
          </thead>
          <tbody>
            {(data?.rows ?? []).map((r, i) => (
              <tr key={i}>
                <td>{r.provider}</td>
                <td className="mono">{r.model}</td>
                <td>{r.requests.toLocaleString()}</td>
                <td>{r.inputTokens.toLocaleString()}</td>
                <td>{r.outputTokens.toLocaleString()}</td>
                <td>{r.totalTokens.toLocaleString()}</td>
                <td className="dim">{r.lastActivityLocal}</td>
              </tr>
            ))}
            {(!data || data.rows.length === 0) && <tr><td colSpan={7} className="wimux-empty">No agent activity</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="wimux-panel-footer">
        <span className="dim">{snap ? `Generated ${new Date(snap.generatedAtUtc).toLocaleString()}` : "Loading…"}</span>
        <span className="wimux-spacer" />
      </div>
    </div>
  );
}
