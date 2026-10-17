import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { Logo } from "../../ui";

/** TK-285: SHA-256 (hex) of the site's access code, set at build time (vite.config.ts); "" = no gate. Read on each call so tests can stub it. */
export const siteGate = (): string => import.meta.env.VITE_SITE_GATE ?? "";

const key = "abastalia.siteGate";
const remembered = () => {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
};

export async function sha256(text: string) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Before launch the public site sits behind an access code. Client-side only: it keeps the site out of search engines and casual eyes,
 * not out of the downloaded JavaScript. The prerendered HTML is this screen; a remembered code opens the site right after hydration. */
export function SiteGate({ children }: { children: ReactNode }) {
  const gate = siteGate();
  const [open, setOpen] = useState(!gate);
  const [code, setCode] = useState("");
  const [tries, setTries] = useState(0);
  const [busy, setBusy] = useState(false);
  const [wrong, setWrong] = useState(false);
  // In an effect, not in useState: the first render has to match the prerendered gate.
  useEffect(() => {
    if (gate && remembered() === gate) setOpen(true);
  }, [gate]);

  if (open) return children;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    // After three wrong codes each try waits a second, so guessing by hand is slow.
    if (tries >= 3) await new Promise((r) => setTimeout(r, 1000));
    const ok = (await sha256(code.trim())) === gate;
    setBusy(false);
    if (!ok) {
      setTries(tries + 1);
      setWrong(true);
      return;
    }
    try {
      localStorage.setItem(key, gate);
    } catch {
      // Private window or blocked storage: the site opens anyway, it asks again next time.
    }
    setOpen(true);
  }

  return (
    <div className="esc-admin auth">
      <header className="auth__brand">
        <div className="auth__top">
          <Logo size={36} variant="on-dark" />
        </div>
        <div className="auth__headline">
          <h1>Sitio en preparación</h1>
          <p>Escriba el código de acceso que le dimos.</p>
        </div>
      </header>
      <main className="auth__main">
        <div className="auth__center">
          <form className="auth__card auth-form" onSubmit={submit}>
            <div className="auth-field">
              <label htmlFor="gate-code">Código de acceso</label>
              <input
                id="gate-code"
                className="auth-input"
                type="password"
                autoComplete="off"
                required
                value={code}
                onChange={(e) => (setCode(e.target.value), setWrong(false))}
                aria-invalid={wrong || undefined}
                aria-describedby={wrong ? "gate-error" : undefined}
              />
              {wrong && (
                <p id="gate-error" className="auth-error" role="alert">
                  Ese código no es correcto. Revíselo e intente de nuevo.
                </p>
              )}
            </div>
            <button type="submit" className="esc-button esc-button--primary auth-submit" disabled={busy}>
              {busy ? "Un momento…" : "Entrar"}
            </button>
          </form>
        </div>
      </main>
    </div>
  );
}
