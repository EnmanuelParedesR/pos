import { useState, type FormEvent } from "react";
import { ApiError, identityMessages, register, signIn } from "../api";
import { Logo, Notice } from "../ui";

/** Entrar / Crear cuenta. Creating an account also creates the owner's business (tenant). */
export function SignIn({ onSignedIn }: { onSignedIn: () => Promise<unknown> }) {
  const [creating, setCreating] = useState(false);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await (creating ? register : signIn)(email.trim(), password);
      await onSignedIn();
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));
      setBusy(false);
    }
  }

  const messages = error ? identityMessages(error) : [];

  return (
    <main className="signin">
      <form className="esc-card esc-card--padded signin__card" onSubmit={submit}>
        <Logo size={44} />
        <div className="esc-row" role="group" aria-label="Opción">
          <button type="button" className="esc-chip" aria-pressed={!creating} onClick={() => (setCreating(false), setError(null))}>
            Entrar
          </button>
          <button type="button" className="esc-chip" aria-pressed={creating} onClick={() => (setCreating(true), setError(null))}>
            Crear cuenta
          </button>
        </div>
        <h1 className="esc-title">{creating ? "Crear su cuenta" : "Entrar al panel"}</h1>
        <label className="esc-field">
          <span>Correo</span>
          <input className="esc-input" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </label>
        <label className="esc-field">
          <span>Contraseña</span>
          <input
            className="esc-input"
            type="password"
            autoComplete={creating ? "new-password" : "current-password"}
            required
            minLength={creating ? 8 : undefined}
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            aria-invalid={error ? true : undefined}
          />
          {creating && <span className="esc-subtitle">Mínimo 8 caracteres, con una mayúscula, una minúscula y un número.</span>}
        </label>
        {error && (
          <Notice kind={error.offline ? "warning" : "error"} title={messages[0]}>
            {messages.length > 1 ? messages.slice(1).join(" ") : undefined}
          </Notice>
        )}
        <button type="submit" className="esc-button esc-button--primary esc-button--wide" disabled={busy}>
          {busy ? "Un momento…" : creating ? "Crear cuenta" : "Entrar"}
        </button>
      </form>
    </main>
  );
}
