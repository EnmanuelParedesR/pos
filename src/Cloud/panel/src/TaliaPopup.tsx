import { useEffect, useRef, type KeyboardEvent, type ReactNode } from "react";
import { Icon, Talia } from "./ui";

/** TK-300: Talia one tap away. A floating button and a modal dialog (focus trapped, Esc closes, the focus goes back to the button) with the chat as
 *  `children`. It stays mounted while closed, so the conversation survives moving between screens. TK-315: the panel and the site both use it. */
export function TaliaPopup({ open, onOpen, onClose, subtitle, children }: { open: boolean; onOpen: () => void; onClose: () => void; subtitle: string; children: ReactNode }) {
  const fab = useRef<HTMLButtonElement>(null);
  const box = useRef<HTMLDivElement>(null);
  const wasOpen = useRef(false);

  useEffect(() => {
    if (open) {
      // A computer can type at once; on a phone the keyboard would cover the suggestions, so focus goes to the dialog.
      // No input yet (the site's chat is lazy and focuses itself when it mounts): the dialog, never the body.
      const wide = window.matchMedia?.("(min-width: 761px)").matches;
      ((wide && box.current?.querySelector<HTMLElement>("input")) || box.current)?.focus();
      const overflow = document.body.style.overflow;
      document.body.style.overflow = "hidden";
      wasOpen.current = true;
      return () => {
        document.body.style.overflow = overflow;
      };
    }
    if (wasOpen.current) fab.current?.focus();
  }, [open]);

  // Esc closes; Tab and Shift+Tab stay inside the dialog.
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "Escape") return (e.stopPropagation(), onClose());
    if (e.key !== "Tab") return;
    const items = [...box.current!.querySelectorAll<HTMLElement>("button:not([disabled]), input, a[href]")];
    const first = items[0];
    const last = items[items.length - 1];
    if (e.shiftKey && (document.activeElement === first || document.activeElement === box.current)) {
      e.preventDefault();
      last.focus();
    } else if (!e.shiftKey && document.activeElement === last) {
      e.preventDefault();
      first.focus();
    }
  };

  return (
    <>
      <button ref={fab} type="button" className="talia-fab" hidden={open} aria-label="Pregúntale a Talia" title="Pregúntale a Talia" aria-haspopup="dialog" onClick={onOpen}>
        <Talia size={56} />
      </button>
      <div className="talia-pop" hidden={!open}>
        <div className="talia-pop__backdrop" onClick={onClose} />
        <div ref={box} className="talia-pop__box" role="dialog" aria-modal="true" aria-labelledby="talia-title" tabIndex={-1} onKeyDown={onKeyDown}>
          <header className="talia-pop__head">
            <Talia size={36} />
            <div>
              <h2 id="talia-title">Talia</h2>
              <span>{subtitle}</span>
            </div>
            <button type="button" className="esc-button esc-button--ghost esc-button--small talia-pop__close" aria-label="Cerrar" onClick={onClose}>
              <Icon name="x" size={20} />
            </button>
          </header>
          {children}
        </div>
      </div>
    </>
  );
}
