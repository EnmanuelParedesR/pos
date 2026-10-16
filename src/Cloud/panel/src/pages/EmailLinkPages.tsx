import { useEffect, useState, type FormEvent } from "react";
import { Link, useLocation } from "react-router";
import { ApiError, confirmEmail, identityMessages, resetPassword } from "../api";
import { Notice, Skeleton } from "../ui";
import { AuthFrame, PasswordChecklist, PasswordField } from "./AuthFrame";

/** The values after "#" of an e-mailed link: like the invite (R8 #12), the code never travels in a page address a server logs. */
const useHashParams = () => new URLSearchParams(useLocation().hash.slice(1));

const toApiError = (err: unknown) => (err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));

/** TK-230: /app/restablecer#email=…&code=… from the "¿La olvidó?" e-mail: a new password, then Entrar. */
export function ResetPasswordPage() {
  const params = useHashParams();
  const email = params.get("email") ?? "";
  const code = params.get("code") ?? "";
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await resetPassword(email, code, password);
      setDone(true);
    } catch (err) {
      setError(toApiError(err));
    }
    setBusy(false);
  }

  const messages = error ? identityMessages(error) : [];

  return (
    <AuthFrame title="Una contraseña nueva y listo.">
      <div className="auth-form">
        <div className="auth-form__head">
          <h2>Cambie su contraseña</h2>
          {email && <p>Para {email}</p>}
        </div>
        {!email || !code ? (
          <>
            <Notice kind="error" title="El enlace está incompleto.">
              Ábralo de nuevo desde el correo, o pida otro en Entrar → ¿La olvidó?
            </Notice>
            <Link className="esc-button esc-button--primary auth-submit" to="/app">
              Ir a Entrar
            </Link>
          </>
        ) : done ? (
          <>
            <Notice kind="success" title="Listo, su contraseña cambió." />
            <Link className="esc-button esc-button--primary auth-submit" to="/app">
              Entrar
            </Link>
          </>
        ) : (
          <form className="auth-form" onSubmit={submit}>
            {error && (
              <Notice kind={error.offline ? "warning" : "error"} title={messages[0]}>
                {messages.length > 1 ? messages.slice(1).join(" ") : undefined}
              </Notice>
            )}
            <PasswordField
              id="reset-password"
              label="Contraseña nueva"
              value={password}
              onChange={setPassword}
              autoComplete="new-password"
              invalid={error !== null && !error.offline}
              describedBy="reset-checks"
            >
              <PasswordChecklist id="reset-checks" password={password} />
            </PasswordField>
            <button type="submit" className="esc-button esc-button--primary auth-submit" disabled={busy}>
              {busy ? "Un momento…" : "Guardar contraseña"}
            </button>
          </form>
        )}
      </div>
    </AuthFrame>
  );
}

/** TK-230: /app/confirmar#userId=…&code=… from the sign-up e-mail. A confirmed address can later get a "¿La olvidó?" e-mail. */
export function ConfirmEmailPage() {
  const params = useHashParams();
  const userId = params.get("userId") ?? "";
  const code = params.get("code") ?? "";
  const [state, setState] = useState<{ done?: boolean; error?: ApiError }>({});

  useEffect(() => {
    let live = true;
    confirmEmail(userId, code).then(
      () => live && setState({ done: true }),
      (err) => live && setState({ error: toApiError(err) }),
    );
    return () => {
      live = false;
    };
  }, [userId, code]);

  return (
    <AuthFrame title="Sus tiendas, aunque no esté en el mostrador.">
      <div className="auth-form">
        <div className="auth-form__head">
          <h2>Confirmar su correo</h2>
        </div>
        {!state.done && !state.error && <Skeleton height={56} />}
        {state.done && <Notice kind="success" title="Listo, su correo quedó confirmado." />}
        {state.error && <Notice kind={state.error.offline ? "warning" : "error"} title={state.error.detail} />}
        <Link className="esc-button esc-button--primary auth-submit" to="/app">
          Ir al panel
        </Link>
      </div>
    </AuthFrame>
  );
}
