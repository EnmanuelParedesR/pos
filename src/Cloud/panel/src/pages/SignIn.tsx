import { useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { ApiError, forgotPassword, identityMessages, register, resendConfirmation, signIn } from "../api";
import { claimReferral } from "../referral";
import { Notice } from "../ui";
import { AuthFrame, PasswordChecklist, PasswordField } from "./AuthFrame";
import { contactHref, site } from "./site/config";

type Mode = "in" | "create";
const tabs: [Mode, string][] = [
  ["in", "Entrar"],
  ["create", "Crear cuenta"],
];

const toApiError = (err: unknown) => (err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));

/** Entrar / Crear cuenta (TK-230 design). Creating an account also creates the owner's business (tenant). "¿La olvidó?" asks for a reset e-mail. */
export function SignIn({ onSignedIn }: { onSignedIn: () => Promise<unknown> }) {
  const [mode, setMode] = useState<Mode>("in");
  const [forgot, setForgot] = useState(false);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [resetSent, setResetSent] = useState(false);
  // R11 B-High1: after "Crear cuenta" (or an Entrar refused for an unconfirmed e-mail) the person confirms the address from the e-mail first.
  const [checkEmail, setCheckEmail] = useState(false);
  // R11 B-Med6: an account with two-step verification asks for the app's code after the password.
  const [needsCode, setNeedsCode] = useState(false);
  const [code, setCode] = useState("");
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const creating = mode === "create";

  const choose = (m: Mode) => (setMode(m), setError(null), setCheckEmail(false));

  /** Tabs pattern: arrows, Home and End move and select. */
  function onTabKey(e: KeyboardEvent) {
    const i = tabs.findIndex(([m]) => m === mode);
    const next = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: tabs.length - 1 }[e.key];
    if (next === undefined) return;
    e.preventDefault();
    const j = (next + tabs.length) % tabs.length;
    choose(tabs[j][0]);
    tabRefs.current[j]?.focus();
  }

  async function run(action: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await action();
    } catch (err) {
      setError(toApiError(err));
    }
    setBusy(false);
  }

  function submit(e: FormEvent) {
    e.preventDefault();
    if (forgot) return run(() => forgotPassword(email.trim()).then(() => setResetSent(true)));
    if (creating) return run(() => register(email.trim(), password).then(() => (setMode("in"), setCheckEmail(true))));
    return run(async () => {
      try {
        await signIn(email.trim(), password, needsCode ? code.trim() : undefined);
      } catch (err) {
        if (err instanceof ApiError && err.code === "Auth.RequiresTwoFactor") setNeedsCode(true);
        if (err instanceof ApiError && err.code === "Auth.NotAllowed") setCheckEmail(true);
        throw err;
      }
      await claimReferral(); // TK-276
      await onSignedIn();
    });
  }

  const messages = error ? identityMessages(error) : [];
  const notice = error && error.code !== "Auth.RequiresTwoFactor" && (
    <Notice kind={error.offline ? "warning" : "error"} title={messages[0]}>
      {messages.length > 1 ? messages.slice(1).join(" ") : undefined}
    </Notice>
  );
  const confirmNotice = checkEmail && (
    <Notice kind="info" title="Confirme su correo para entrar.">
      Le enviamos un enlace a {email.trim()}. Ábralo y luego entre con su contraseña. Si no lo ve, busque en el correo no deseado o{" "}
      <button type="button" className="auth-link" onClick={() => void resendConfirmation(email.trim()).catch(() => undefined)}>
        pida otro
      </button>
      .
    </Notice>
  );

  const emailField = (
    <div className="auth-field">
      <label htmlFor="signin-email">Correo</label>
      <input
        id="signin-email"
        className="auth-input"
        type="email"
        inputMode="email"
        autoComplete="email"
        placeholder="usted@sunegocio.com"
        required
        value={email}
        onChange={(e) => (setEmail(e.target.value), setNeedsCode(false))}
      />
    </div>
  );

  return (
    <AuthFrame title="Sus tiendas, aunque no esté en el mostrador." extra={<BrandExample />}>
      {forgot ? (
        <form className="auth-form" onSubmit={submit}>
          <div className="auth-form__head">
            <h2>Cambie su contraseña</h2>
            <p>Le mandamos un enlace a su correo para crear una nueva.</p>
          </div>
          {resetSent ? (
            <Notice kind="success" title="Revise su correo.">
              Si hay una cuenta con {email.trim()}, le llegará un enlace en unos minutos. Si no lo ve, busque en el correo no deseado.
            </Notice>
          ) : (
            <>
              {notice}
              {emailField}
              <button type="submit" className="esc-button esc-button--primary auth-submit" disabled={busy}>
                {busy ? "Un momento…" : "Mandar enlace"}
              </button>
            </>
          )}
          <p className="auth-note">
            Si no le llega el correo, puede que su cuenta no tenga el correo confirmado.{" "}
            <a href={contactHref}>{site.whatsapp ? "Escríbanos por WhatsApp" : "Escríbanos"}</a> y le ayudamos.
          </p>
          <button type="button" className="auth-link auth-link--block" onClick={() => (setForgot(false), setResetSent(false), setError(null))}>
            ← Volver a Entrar
          </button>
        </form>
      ) : (
        <>
          <div className="auth-tabs" role="tablist" aria-label="Opción" onKeyDown={onTabKey}>
            {tabs.map(([m, text], i) => (
              <button
                key={m}
                ref={(el) => {
                  tabRefs.current[i] = el;
                }}
                id={`signin-tab-${m}`}
                type="button"
                role="tab"
                aria-selected={m === mode}
                aria-controls="signin-panel"
                tabIndex={m === mode ? 0 : -1}
                onClick={() => choose(m)}
              >
                {text}
              </button>
            ))}
          </div>
          <form id="signin-panel" className="auth-form" role="tabpanel" aria-labelledby={`signin-tab-${mode}`} onSubmit={submit}>
            <div className="auth-form__head">
              <h2>{creating ? "Cree su cuenta" : "Bienvenido de nuevo"}</h2>
              <p>{creating ? "14 días de prueba. Empiezan a contar con su primera venta." : "Entre para ver cómo van sus tiendas."}</p>
            </div>
            {confirmNotice}
            {notice}
            {emailField}
            <PasswordField
              id="signin-password"
              value={password}
              onChange={setPassword}
              autoComplete={creating ? "new-password" : "current-password"}
              invalid={error !== null && !error.offline && error.code !== "Auth.RequiresTwoFactor" && error.code !== "Auth.NotAllowed"}
              describedBy={creating ? "signin-checks" : undefined}
              aside={
                !creating && (
                  <button type="button" className="auth-link" onClick={() => (setForgot(true), setError(null))}>
                    ¿La olvidó?
                  </button>
                )
              }
            >
              {creating && <PasswordChecklist id="signin-checks" password={password} />}
            </PasswordField>
            {needsCode && !creating && (
              <div className="auth-field">
                <label htmlFor="signin-code">Código de verificación</label>
                <input
                  id="signin-code"
                  className="auth-input"
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  pattern="[0-9 ]{6,7}"
                  placeholder="123456"
                  required
                  autoFocus
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  aria-describedby="signin-code-hint"
                />
                <p id="signin-code-hint" className="auth-note">El de 6 dígitos que muestra su aplicación de autenticación.</p>
              </div>
            )}
            <button type="submit" className="esc-button esc-button--primary auth-submit" disabled={busy}>
              {busy ? "Un momento…" : creating ? "Crear cuenta" : "Entrar"}
            </button>
            {creating ? (
              <p className="auth-note">
                {/* shortcut: the site has no terms or privacy pages yet; these addresses show its 404 until they exist. */}
                Al crear la cuenta acepta los <a href="/terminos">términos</a> y la <a href="/privacidad">política de privacidad</a>. No pedimos tarjeta.
              </p>
            ) : (
              <p className="auth-note">¿Lo invitaron al equipo de una tienda? Abra el enlace del correo de invitación.</p>
            )}
          </form>
        </>
      )}
    </AuthFrame>
  );
}

/** Desktop only: what the panel shows, with sample numbers (decorative). */
function BrandExample() {
  return (
    <>
      <p className="auth__lead">Vea las ventas del día, los cuadres de caja y los comprobantes de la DGII desde el celular. Los datos llegan solos desde la caja.</p>
      <div className="auth-example" aria-hidden="true">
        <div className="auth-example__top">
          <span>VENTAS DE HOY · EJEMPLO</span>
          <span>3:41 p. m.</span>
        </div>
        <div className="auth-example__amount">
          <span>RD$</span>
          <b>1,623.25</b>
        </div>
        <ul>
          <li>
            <span>Caja 1 · cuadró</span>
            <span className="auth-example__ok">Sin diferencia</span>
          </li>
          <li>
            <span>Comprobantes e-CF</span>
            <span>Todos enviados</span>
          </li>
        </ul>
      </div>
      <p className="auth__foot">Para carnicerías, colmados y delis · República Dominicana</p>
    </>
  );
}
