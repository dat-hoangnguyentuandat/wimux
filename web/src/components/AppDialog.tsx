import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";

type DialogKind = "alert" | "confirm" | "prompt";

interface DialogState {
  kind: DialogKind;
  title: string;
  message?: string;
  initialValue?: string;
  confirmText?: string;
  cancelText?: string;
  resolve: (value: string | boolean | null) => void;
}

interface AppDialogApi {
  alert: (title: string, message?: string) => Promise<void>;
  confirm: (title: string, message?: string, confirmText?: string) => Promise<boolean>;
  prompt: (title: string, initialValue?: string, message?: string) => Promise<string | null>;
}

const DialogContext = createContext<AppDialogApi | null>(null);

export function useAppDialog() {
  const ctx = useContext(DialogContext);
  if (!ctx) throw new Error("useAppDialog must be used inside AppDialogProvider");
  return ctx;
}

export function AppDialogProvider({ children }: { children: ReactNode }) {
  const [dialog, setDialog] = useState<DialogState | null>(null);
  const [value, setValue] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);

  const open = useCallback((state: Omit<DialogState, "resolve">) => new Promise<string | boolean | null>((resolve) => {
    setValue(state.initialValue ?? "");
    setDialog({ ...state, resolve });
  }), []);

  const alert = useCallback(async (title: string, message?: string) => {
    await open({ kind: "alert", title, message, confirmText: "OK" });
  }, [open]);

  const confirm = useCallback(async (title: string, message?: string, confirmText = "OK") => {
    const result = await open({ kind: "confirm", title, message, confirmText, cancelText: "Cancel" });
    return result === true;
  }, [open]);

  const prompt = useCallback(async (title: string, initialValue = "", message?: string) => {
    const result = await open({ kind: "prompt", title, message, initialValue, confirmText: "Save", cancelText: "Cancel" });
    return typeof result === "string" ? result : null;
  }, [open]);

  useEffect(() => {
    if (dialog?.kind === "prompt") {
      const id = window.setTimeout(() => inputRef.current?.select(), 0);
      return () => window.clearTimeout(id);
    }
  }, [dialog?.kind]);

  const close = (result: string | boolean | null) => {
    const d = dialog;
    setDialog(null);
    d?.resolve(result);
  };

  return (
    <DialogContext.Provider value={{ alert, confirm, prompt }}>
      {children}
      {dialog && (
        <div className="wimux-popup-overlay" onMouseDown={(e) => { if (e.target === e.currentTarget) close(dialog.kind === "alert" ? true : null); }}>
          <div className="wimux-dialog" onMouseDown={(e) => e.stopPropagation()}>
            <div className="wimux-dialog-title">{dialog.title}</div>
            {dialog.message && <div className="wimux-dialog-message">{dialog.message}</div>}
            {dialog.kind === "prompt" && (
              <input
                ref={inputRef}
                className="wimux-dialog-input"
                value={value}
                onChange={(e) => setValue(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") close(value.trim());
                  if (e.key === "Escape") close(null);
                }}
              />
            )}
            <div className="wimux-dialog-actions">
              {dialog.kind !== "alert" && <button className="wimux-btn" onClick={() => close(null)}>{dialog.cancelText ?? "Cancel"}</button>}
              <button className="wimux-btn primary" onClick={() => close(dialog.kind === "prompt" ? value.trim() : true)}>
                {dialog.confirmText ?? "OK"}
              </button>
            </div>
          </div>
        </div>
      )}
    </DialogContext.Provider>
  );
}
