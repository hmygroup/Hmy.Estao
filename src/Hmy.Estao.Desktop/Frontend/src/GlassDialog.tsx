import { useLayoutEffect, useId, useRef, type MouseEvent, type ReactNode } from "react";
import { createRoot, type Root } from "react-dom/client";
import { t } from "./i18n";

/* In-page replacement for WebView2's native confirmation boxes.
   A native <dialog> opened with showModal() supplies the top layer, inert
   background, focus containment, Escape-to-cancel and focus restoration;
   this module only adds the product's glass styling and a Promise API. */

interface GlassDialogOptions {
  title: string;
  message: string;
  confirmLabel?: string;
  /** Destructive actions start focused on Cancel and use the warning style. */
  tone?: "default" | "danger";
}

let host: Root | undefined;
let open = false;
let dialogId = 0;

function render(node: ReactNode) {
  if (!host) {
    const element = document.createElement("div");
    element.className = "glass-dialog-host";
    document.body.appendChild(element);
    host = createRoot(element);
  }
  host.render(node);
}

/** Resolves true when the user confirms; false on Cancel, Escape or a backdrop click.
    Only one dialog is shown at a time: a request made while another is open resolves false. */
export function confirmDialog(options: GlassDialogOptions): Promise<boolean> {
  if (open || typeof document === "undefined") return Promise.resolve(false);
  open = true;
  const returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  return new Promise<boolean>(resolve => {
    const finish = (result: boolean) => {
      open = false;
      render(null);
      if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
      resolve(result);
    };
    render(<GlassDialog key={++dialogId} {...options} onClose={finish} />);
  });
}

function GlassDialog({ title, message, confirmLabel, tone = "default", onClose }: GlassDialogOptions & { onClose: (result: boolean) => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const initialFocus = useRef<HTMLButtonElement>(null);
  const settled = useRef(false);
  const titleId = useId();
  const messageId = useId();
  const close = (result: boolean) => {
    if (settled.current) return;
    settled.current = true;
    if (dialog.current?.open) dialog.current.close();
    onClose(result);
  };
  useLayoutEffect(() => {
    const element = dialog.current;
    if (element && !element.open) element.showModal();
    initialFocus.current?.focus({ preventScroll: true });
  }, []);
  // A click whose target is the <dialog> itself landed on the ::backdrop.
  const backdropClick = (event: MouseEvent<HTMLDialogElement>) => { if (event.target === event.currentTarget) close(false); };
  return <dialog ref={dialog} className={`glass-dialog${tone === "danger" ? " danger" : ""}`}
    role="alertdialog" aria-modal="true" aria-labelledby={titleId} aria-describedby={messageId}
    onCancel={event => { event.preventDefault(); close(false); }} onClick={backdropClick}>
    <div className="glass-dialog-panel">
      <h2 id={titleId}>{title}</h2>
      <p id={messageId}>{message}</p>
      <div className="glass-dialog-actions">
        <button type="button" className="secondary" ref={tone === "danger" ? initialFocus : undefined} onClick={() => close(false)}>{t("cancel")}</button>
        <button type="button" className={tone === "danger" ? "secondary glass-dialog-danger" : "primary"} ref={tone !== "danger" ? initialFocus : undefined}
          onClick={() => close(true)}>{confirmLabel ?? t("dialog.ok")}</button>
      </div>
    </div>
  </dialog>;
}
