import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from "react";
import { useLocation } from "react-router";
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

/** TK-297: what /app/confirmar hands over (router state, never the address): the account is active, the e-mail to fill in. */
export interface SignInArrival {
  activated?: boolean;
  email?: string;
}

const toApiError = (err: unknown) => (err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));
const helpLink = <a href={contactHref}>{site.whatsapp ? "Escríbanos por WhatsApp" : "Escríbanos"}</a>;

/** Entrar / Crear cuenta (TK-230 design). Creating an account also creates the owner's business (tenant). "¿La olvidó?" asks for a reset e-mail. */
export function SignIn({ onSignedIn }: { onSignedIn: () => Promise<unknown> }) {
  const arrival = (useLocation().state ?? {}) as SignInArrival;
  const [mode, setMode] = useState<Mode>("in");
  const [forgot, setForgot] = useState(false);
  const [email, setEmail] = useState(arrival.email ?? "");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [resetSent, setResetSent] = useState(false);
  // R11 B-High1 / TK-297: after "Crear cuenta" the person activates the account from the e-mail first; this screen says so and where it went.
  const [sentTo, setSentTo] = useState<string | null>(null);
  // An Entrar refused for an unconfirmed e-mail.
  const [checkEmail, setCheckEmail] = useState(false);
  // R11 B-Med6: an account with two-step verification asks for the app's code after the password; TK-298: or a backup code.
  const [needsCode, setNeedsCode] = useState(false);
  const [useBackup, setUseBackup] = useState(false);
  const [code, setCode] = useState("");
  const tabRefs = useRef<(HTMLButtonElement | null)[]>([]);
  const passwordRef = useRef<HTMLInputElement>(null);
  const creating = mode === "create";

  // TK-297: from the activation link, only the password is left to type.
  useEffect(() => {
    if (arrival.activated && arrival.email) passwordRef.current?.focus();
  }, [arrival.activated, arrival.email]);

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
    if (creating) return run(() => register(email.trim(), password).then(() => (setSentTo(email.trim()), setPassword(""))));
    return run(async () => {
      try {
        const second = needsCode ? code.trim() : undefined;
        await signIn(email.trim(), password, useBackup ? undefined : second, useBackup ? second : undefined);
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
  const confirmNotice = checkEmail && !error?.offline && (
    <Notice kind="info" title="Active su cuenta desde el correo.">
      Busque el correo «Confirme su correo de Abastalia» que mandamos a {email.trim()} y abra el enlace. Si no lo ve, busque en el correo no deseado.{" "}
      <ResendButton email={email.trim()} />
    </Notice>
  );
  const activatedNotice = arrival.activated && mode === "in" && !error && !needsCode && (
    <Notice kind="success" title="Su cuenta quedó activada.">
      {arrival.email ? "Escriba su contraseña para entrar." : "Entre con su correo y su contraseña."}
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
        onChange={(e) => (setEmail(e.target.value), setNeedsCode(false), setCheckEmail(false))}
      />
    </div>
  );

  return (
    <AuthFrame title="Sus tiendas, aunque no esté en el mostrador." extra={<BrandExample />}>
      {sentTo ? (
        <CheckEmail
          email={sentTo}
          onSignIn={() => (setSentTo(null), choose("in"))}
          onWrongEmail={() => setSentTo(null)}
        />
      ) : forgot ? (
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
            Si no le llega el correo, puede que su cuenta no tenga el correo confirmado. {helpLink} y le ayudamos.
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
            {activatedNotice}
            {confirmNotice}
            {notice}
            {emailField}
            <PasswordField
              id="signin-password"
              inputRef={passwordRef}
              value={password}
              onChange={setPassword}
              autoComplete={creating ? "new-password" : "current-password"}
              invalid={error !== null && !error.offline && !needsCode && error.code !== "Auth.RequiresTwoFactor" && error.code !== "Auth.NotAllowed"}
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
                <label htmlFor="signin-code">{useBackup ? "Código de respaldo" : "Código de verificación"}</label>
                <input
                  key={useBackup ? "backup" : "app"}
                  id="signin-code"
                  className="auth-input"
                  inputMode={useBackup ? "text" : "numeric"}
                  autoComplete={useBackup ? "off" : "one-time-code"}
                  autoCapitalize={useBackup ? "characters" : undefined}
                  pattern={useBackup ? undefined : "[0-9 ]{6,7}"}
                  placeholder={useBackup ? "XXXXX-XXXXX" : "123456"}
                  required
                  autoFocus
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                  aria-describedby="signin-code-hint"
                />
                <p id="signin-code-hint" className="auth-note">
                  {useBackup
                    ? "Uno de los códigos que guardó al activar la verificación. Cada uno sirve una sola vez."
                    : "El de 6 dígitos que muestra su aplicación de autenticación."}
                </p>
                <button type="button" className="auth-link auth-link--block" onClick={() => (setUseBackup(!useBackup), setCode(""), setError(null))}>
                  {useBackup ? "Usar el código de la aplicación" : "¿No tiene el teléfono? Use un código de respaldo"}
                </button>
                {useBackup && <p className="auth-note">¿Perdió el teléfono y los códigos? {helpLink} para recuperar el acceso.</p>}
              </div>
            )}
            <button type="submit" className="esc-button esc-button--primary auth-submit" disabled={busy}>
              {busy ? (creating ? "Creando su cuenta…" : "Un momento…") : creating ? "Crear cuenta" : "Entrar"}
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

/** TK-231/TK-297: one more confirmation e-mail. The API answers the same whether or not anything was sent, and limits it (per IP and per address). */
function ResendButton({ email }: { email: string }) {
  const [state, setState] = useState<"idle" | "busy" | "sent" | "limited" | "failed">("idle");
  async function resend() {
    setState("busy");
    try {
      await resendConfirmation(email);
      setState("sent");
    } catch (err) {
      setState(err instanceof ApiError && err.status === 429 ? "limited" : "failed");
    }
  }
  if (state === "sent") return <span role="status">Listo, le mandamos otro. Puede tardar unos minutos.</span>;
  return (
    <>
      {state === "limited" && <span role="status">Ya pedimos varios correos. Espere unos minutos e intente de nuevo. </span>}
      {state === "failed" && <span role="status">No se pudo mandar. Revise el internet e intente de nuevo. </span>}
      <button type="button" className="auth-link" disabled={state === "busy"} onClick={() => void resend()}>
        {state === "busy" ? "Mandando…" : "Mandar el correo otra vez"}
      </button>
    </>
  );
}

/** TK-297: right after "Crear cuenta". The account cannot sign in until the e-mail is confirmed (R11 B-High1), so the next step is the inbox. */
function CheckEmail({ email, onSignIn, onWrongEmail }: { email: string; onSignIn: () => void; onWrongEmail: () => void }) {
  return (
    <div className="auth-form">
      <div className="auth-form__head">
        <h2>Revise su correo</h2>
        <p>
          Le mandamos un enlace a <b className="auth-email">{email}</b>. Ábralo para activar su cuenta y luego entre con su contraseña.
        </p>
      </div>
      <p className="auth-note auth-note--big">El correo se llama «Confirme su correo de Abastalia». El enlace sirve por un día.</p>
      <section className="auth-help-box" aria-labelledby="signin-noemail">
        <h3 id="signin-noemail">¿No le llegó?</h3>
        <ul>
          <li>Puede tardar unos minutos. Busque también en el correo no deseado o en promociones.</li>
          <li>
            ¿El correo está mal escrito?{" "}
            <button type="button" className="auth-link" onClick={onWrongEmail}>
              Corríjalo
            </button>
          </li>
          <li>
            <ResendButton email={email} />
          </li>
          <li>¿Sigue sin llegar? {helpLink} y le ayudamos.</li>
        </ul>
      </section>
      {/* Not primary: the next step is the inbox, not this page. */}
      <button type="button" className="esc-button auth-submit" onClick={onSignIn}>
        Ya la activé: entrar
      </button>
    </div>
  );
}

/** Desktop only: what the panel shows, with sample numbers of a made-up minimarket (decorative). */
function BrandExample() {
  return (
    <>
      <p className="auth__lead">Vea las ventas del día, los cuadres de caja y los comprobantes de la DGII desde el celular. Los datos llegan solos desde la caja.</p>
      <div className="auth-example" aria-hidden="true">
        <div className="auth-example__top">
          <span>MINIMERCADO DE EJEMPLO · HOY</span>
          <span>3:41 p. m.</span>
        </div>
        <div className="auth-example__amount">
          <span>RD$</span>
          <b>48,215.50</b>
        </div>
        <ul>
          <li>
            <span>Cajas 1 y 2 · cuadraron</span>
            <span className="auth-example__ok">Sin diferencia</span>
          </li>
          <li>
            <span>Carnes y embutidos</span>
            <span>34 % de la venta</span>
          </li>
          <li>
            <span>Comprobantes e-CF</span>
            <span>Todos enviados</span>
          </li>
        </ul>
      </div>
      <p className="auth__foot">Para minimercados, carnicerías y delis · República Dominicana</p>
    </>
  );
}
