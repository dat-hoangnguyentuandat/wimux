import { useEffect, useMemo, useState } from "react";
import { api, type WorkspaceTemplate } from "../lib/api";
import { useAppDialog } from "./AppDialog";

interface Props {
  onClose: () => void;
  workspaceId?: string;
  workspaceName?: string;
  onApplied?: () => void;
}

export function TemplatesPanel({ onClose, workspaceId, workspaceName, onApplied }: Props) {
  const dialog = useAppDialog();
  const [items, setItems] = useState<WorkspaceTemplate[]>([]);
  const [selected, setSelected] = useState<string | null>(null);

  const load = () => api.getTemplates().then((rows) => { setItems(rows); if (rows[0]) setSelected(rows[0].id); });
  useEffect(() => { load(); }, []);

  const remove = async (id: string) => { await api.deleteTemplate(id); await load(); };
  const apply = async (id: string) => { await api.applyTemplate(id); onApplied?.(); onClose(); };
  const saveCurrent = async () => {
    if (!workspaceId) return;
    const name = await dialog.prompt("Template name", workspaceName || "Template");
    if (!name) return;
    await api.saveTemplateFromWorkspace(workspaceId, name);
    await load();
  };

  const selectedItem = useMemo(() => items.find((t) => t.id === selected), [items, selected]);

  return (
    <div className="cmux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="cmux-popup-panel" onMouseDown={(e) => e.stopPropagation()} style={{ maxWidth: 620, maxHeight: 480 }}>
      <div className="cmux-panel-toolbar">
        <div className="cmux-panel-toolbar-row">
          <span className="cmux-panel-title">WORKSPACE TEMPLATES</span>
          <span className="cmux-spacer" />
          {workspaceId && <button className="cmux-btn" onClick={saveCurrent}>Save current</button>}
          <button className="cmux-icon-btn" onClick={onClose}>×</button>
        </div>
      </div>
      <div className="cmux-panel-body cmux-split-body cmux-wide-split-body" style={{ maxHeight: 380 }}>
        <div className="cmux-list">
          {items.length === 0 && <div className="cmux-empty">No templates saved</div>}
          {items.map((t) => (
            <div
              key={t.id}
              className={"cmux-list-item" + (t.id === selected ? " active" : "")}
              onClick={() => setSelected(t.id)}
            >
              <div className="cmux-list-row">
                <span className="cmux-list-title">{t.name}</span>
                <button className="cmux-icon-btn" onClick={(e) => { e.stopPropagation(); remove(t.id); }}>×</button>
              </div>
              <div className="cmux-list-meta dim">{t.description || `${t.surfaces.length} surface(s)`}</div>
            </div>
          ))}
        </div>
        <div className="cmux-split-divider" />
        <div className="cmux-split-content">
          {selectedItem ? (
            <>
              <div className="cmux-list-title">{selectedItem.name}</div>
              <div className="dim" style={{ marginBottom: 8 }}>{selectedItem.description || "No description"}</div>
              <div className="cmux-list-meta dim">Surfaces</div>
              <ul style={{ margin: 0, paddingLeft: 16 }}>
                {selectedItem.surfaces.map((s, i) => (
                  <li key={i}>{s.name} <span className="dim mono">· {s.panes.length} pane(s)</span></li>
                ))}
              </ul>
              {Object.keys(selectedItem.environmentVariables ?? {}).length > 0 && (
                <>
                  <div className="cmux-list-meta dim" style={{ marginTop: 8 }}>Env vars</div>
                  <pre className="cmux-mono-pre">{Object.entries(selectedItem.environmentVariables).map(([k, v]) => `${k}=${v}`).join("\n")}</pre>
                </>
              )}
            </>
          ) : (
            <div className="cmux-empty">Select a template to preview</div>
          )}
        </div>
      </div>
      <div className="cmux-panel-footer">
        <span className="cmux-spacer" />
        <button className="cmux-btn primary" onClick={() => selected && apply(selected)} disabled={!selected}>Load Selected</button>
      </div>
      </div>
    </div>
  );
}
