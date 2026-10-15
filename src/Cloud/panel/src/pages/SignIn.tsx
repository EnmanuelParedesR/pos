import { useState, type FormEvent } from "react";
import { ApiError, identityMessages, register, signIn } from "../api";
import { Logo, Notice, Segmented } from "../ui";

/** Entrar / Crear cuenta. Creating an account also creates the owner's business (tenant). */
export function SignIn({ onSignedIn }: { onSignedIn: () => Promise<unknown> }) {
  const [mode, setMode] = useState<"in" | "create">("in");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const creating = mode === "create";

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
    <main className="esc-admin esc-signin">
      <form className="esc-card esc-signin__card" onSubmit={submit}>
        <Logo size={40} />
        <div>
          <h1>{creating ? "Cree su cuenta" : "Bienvenido de nuevo"}</h1>
          <p className="esc-muted" style={{ marginTop: 4 }}>
            {creating ? "Vea sus ventas y cuadres desde el celular." : "Entre para ver cómo van sus tiendas."}
          </p>
        </div>
        <Segmented<"in" | "create">
          label="Opción"
          options={[
            ["in", "Entrar"],
            ["create", "Crear cuenta"],
          ]}
          value={mode}
          onChange={(m) => (setMode(m), setError(null))}
        />
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
          {creating && <span className="esc-field__hint">Mínimo 8 caracteres, con una mayúscula, una minúscula y un número.</span>}
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
