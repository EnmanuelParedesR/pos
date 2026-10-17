import { useEffect, useState, type FormEvent } from "react";
import { Link, useLocation, useNavigate } from "react-router";
import { ApiError, confirmEmail, identityMessages, resetPassword, type ConfirmedEmail } from "../api";
import { Notice, Skeleton } from "../ui";
import { AuthFrame, PasswordChecklist, PasswordField } from "./AuthFrame";
import type { SignInArrival } from "./SignIn";

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

/** TK-230: /app/confirmar#userId=…&code=… from the sign-up e-mail; it activates the account (R11 B-High1: only a confirmed address signs in).
 * TK-297: it says so in plain words and hands the address to Entrar, so only the password is left. It does not sign in by itself: the link
 * would then be a key to the account for anyone who opens it (a forwarded e-mail, a link scanner), and the password stays the proof. */
export function ConfirmEmailPage() {
  const params = useHashParams();
  const userId = params.get("userId") ?? "";
  const code = params.get("code") ?? "";
  const changedEmail = params.get("changedEmail") ?? undefined;
  const complete = userId !== "" && code !== "";
  const [state, setState] = useState<{ result?: ConfirmedEmail; error?: ApiError }>({});
  const navigate = useNavigate();

  useEffect(() => {
    if (!complete) return;
    let live = true;
    confirmEmail(userId, code, changedEmail).then(
      (result) => live && setState({ result }),
      (err) => live && setState({ error: toApiError(err) }),
    );
    return () => {
      live = false;
    };
  }, [complete, userId, code, changedEmail]);

  const { result, error } = state;
  const arrival: SignInArrival = { activated: result !== undefined, email: result?.email ?? undefined };
  const enter = (
    <button type="button" className="esc-button esc-button--primary auth-submit" onClick={() => navigate("/app", { state: arrival })}>
      Entrar
    </button>
  );

  return (
    <AuthFrame title="Sus tiendas, aunque no esté en el mostrador.">
      <div className="auth-form" aria-live="polite">
        {!complete ? (
          <>
            <div className="auth-form__head">
              <h2>El enlace está incompleto</h2>
              <p>Ábralo de nuevo desde el correo «Confirme su correo de Abastalia».</p>
            </div>
            {enter}
          </>
        ) : result ? (
          <>
            <div className="auth-form__head">
              <h2>{changedEmail ? "Su correo nuevo quedó confirmado" : result.alreadyConfirmed ? "Su cuenta ya estaba activada" : "Su cuenta quedó activada"}</h2>
              <p>
                {result.email ? (
                  <>
                    Ya puede entrar con <b className="auth-email">{result.email}</b> y la contraseña que eligió.
                  </>
                ) : (
                  "Ya puede entrar con su correo y la contraseña que eligió."
                )}
              </p>
            </div>
            {enter}
            {/* R11 B-High1: someone else may have created the account with this e-mail; what they added before is gone, but not their password.
                A note under the button: almost everyone did create it, and the success comes first. */}
            {result.firstConfirmation && (
              <p className="auth-note">
                <b>¿No creó usted esta cuenta?</b> Cambie la contraseña ahora: en Entrar, toque «¿La olvidó?». Por seguridad quitamos del negocio a
                las personas e invitaciones que se agregaron antes de activarla.
              </p>
            )}
          </>
        ) : error ? (
          <>
            <div className="auth-form__head">
              <h2>{error.offline ? "Sin conexión" : "No pudimos activar la cuenta"}</h2>
            </div>
            <Notice kind={error.offline ? "warning" : "error"} title={error.detail}>
              {error.offline
                ? "Vuelva a abrir el enlace cuando tenga internet."
                : "Entre con su correo y su contraseña: si la cuenta todavía no está activada, ahí puede pedir otro enlace."}
            </Notice>
            {enter}
          </>
        ) : (
          <>
            <div className="auth-form__head">
              <h2>Activando su cuenta…</h2>
            </div>
            <Skeleton height={56} />
          </>
        )}
      </div>
    </AuthFrame>
  );
}
