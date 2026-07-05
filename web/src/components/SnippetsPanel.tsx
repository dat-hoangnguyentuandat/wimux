import { useEffect, useState } from "react";
import { api, type Snippet } from "../lib/api";

interface Props {
  onClose: () => void;
  onInsert?: (text: string) => void;
}

export function SnippetsPanel({ onClose, onInsert }: Props) {
  const [items, setItems] = useState<Snippet[]>([]);
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState<Partial<Snippet> | null>(null);
  const [categories, setCategories] = useState<string[]>([]);

  const load = () => api.getSnippets(query).then(setItems);
  useEffect(() => { load(); }, [query]);
  useEffect(() => { api.getSnippetCategories().then(setCategories).catch(() => setCategories([])); }, []);

  const save = async () => {
    if (!editing) return;
    if (editing.id) await api.updateSnippet(editing.id, editing as Snippet);
    else await api.createSnippet(editing);
    setEditing(null);
    await load();
  };
  const remove = async (id: string) => { await api.deleteSnippet(id); await load(); };
  const insert = async (s: Snippet) => { await api.useSnippet(s.id); onInsert?.(s.content); onClose(); };
  const toggleFav = async (s: Snippet) => {
    const updated = { ...s, isFavorite: !s.isFavorite };
    await api.updateSnippet(s.id, updated);
    await load();
  };

  return (
    <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="wimux-popup-panel" onMouseDown={(e) => e.stopPropagation()} style={{ maxWidth: 600, maxHeight: 520 }}>
      <div className="wimux-panel-toolbar">
        <div className="wimux-panel-toolbar-row">
          <span className="wimux-panel-title">SNIPPETS</span>
          <input style={{ flex: 1 }} placeholder="Search snippets..." value={query} onChange={(e) => setQuery(e.target.value)} />
          <button className="wimux-btn" onClick={() => setEditing({ name: "", content: "", category: "General", tags: [], isFavorite: false } as Partial<Snippet>)}>New</button>
          <button className="wimux-icon-btn" onClick={onClose}>×</button>
        </div>
      </div>
      {editing ? (
        <div className="wimux-panel-body">
          <div className="wimux-settings-grid">
            <label className="wimux-field"><span>Name</span><input value={editing.name ?? ""} onChange={(e) => setEditing({ ...editing, name: e.target.value })} /></label>
            <label className="wimux-field"><span>Category</span>
              <input list="wimux-snip-cats" value={editing.category ?? ""} onChange={(e) => setEditing({ ...editing, category: e.target.value })} />
              <datalist id="wimux-snip-cats">{categories.map((c) => <option key={c} value={c} />)}</datalist>
            </label>
            <label className="wimux-field full"><span>Content</span>
              <textarea rows={6} value={editing.content ?? ""} onChange={(e) => setEditing({ ...editing, content: e.target.value })} /></label>
            <label className="wimux-field checkbox"><input type="checkbox" checked={!!editing.isFavorite} onChange={(e) => setEditing({ ...editing, isFavorite: e.target.checked })} /><span>Favorite</span></label>
          </div>
          <div className="wimux-modal-actions">
            <button onClick={() => setEditing(null)}>Cancel</button>
            <button className="primary" onClick={save}>Save</button>
          </div>
        </div>
      ) : (
        <div className="wimux-panel-body">
          {items.length === 0 && <div className="wimux-empty">No snippets</div>}
          {items.map((s) => (
            <div key={s.id} className="wimux-snippet-row">
              <span className={"wimux-fav" + (s.isFavorite ? " on" : "")} onClick={() => toggleFav(s)} title="Toggle favorite">★</span>
              <div className="wimux-snippet-info" onClick={() => insert(s)}>
                <div className="wimux-snippet-name">{s.name} <span className="dim">· {s.category}</span></div>
                <div className="wimux-snippet-content mono dim">{s.content}</div>
              </div>
              <div className="wimux-snippet-actions">
                <button onClick={() => setEditing(s)}>Edit</button>
                <button onClick={() => remove(s.id)}>Delete</button>
              </div>
            </div>
          ))}
        </div>
      )}
      </div>
    </div>
  );
}
