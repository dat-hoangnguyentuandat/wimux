import { useEffect, useRef, useState } from "react";
import { api, type Notification } from "../lib/api";

interface Props {
  onClose: () => void;
  onChanged?: () => void;
}

export function NotificationsPanel({ onClose, onChanged }: Props) {
  const [items, setItems] = useState<Notification[]>([]);

  const load = () => api.getNotifications().then((r) => setItems(r.items));
  useEffect(() => { load(); }, []);

  const markRead = async (id: string) => { await api.markNotificationRead(id); await load(); onChanged?.(); };
  const markAll = async () => { await api.markAllNotificationsRead(); await load(); onChanged?.(); };
  const clear = async () => { await api.clearNotifications(); await load(); onChanged?.(); };

  return (
    <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}>
      <div className="wimux-popup-panel" onMouseDown={(e) => e.stopPropagation()} style={{ maxWidth: 500, maxHeight: 480 }}>
      <div className="wimux-panel-toolbar">
        <div className="wimux-panel-toolbar-row">
          <span className="wimux-panel-title">NOTIFICATIONS</span>
          <span className="wimux-spacer" />
          <button className="wimux-btn" onClick={markAll}>Mark all read</button>
          <button className="wimux-btn" onClick={clear}>Clear</button>
          <button className="wimux-icon-btn" onClick={onClose}>×</button>
        </div>
      </div>
      <div className="wimux-panel-body" style={{ maxHeight: 400, overflow: "auto" }}>
        {items.length === 0 && <div className="wimux-empty">No notifications</div>}
        {items.map((n) => (
          <div
            key={n.id}
            className={"wimux-notif-row" + (n.isRead ? "" : " unread")}
            onClick={() => markRead(n.id)}
          >
            <div className="wimux-notif-title">{n.title}</div>
            {n.subtitle && <div className="wimux-notif-sub">{n.subtitle}</div>}
            <div className="wimux-notif-body">{n.body}</div>
            <div className="wimux-notif-time dim">{new Date(n.timestamp).toLocaleString()}</div>
          </div>
        ))}
      </div>
      </div>
    </div>
  );
}
