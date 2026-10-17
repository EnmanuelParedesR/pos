import { useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { renderSVG } from "uqr";
import { ApiError, signOut, twoFactor, useApi, type Me, type TwoFactorStatus } from "../api";
import { LoadError, Notice, PageHead, Skeleton } from "../ui";
import { contactHref, site } from "./site/config";

/** The key in groups of four, as authenticator apps show it. */
const groups = (key: string) => key.replace(/(.{4})/g, "$1 ").trim();

/** TK-298: what the authenticator app reads from the QR (the Key URI format every app knows). */
export const otpauthUri = (key: string, email: string) =>
  `otpauth://totp/${encodeURIComponent(`Abastalia:${email}`)}?secret=${key}&issuer=Abastalia&digits=6`;

type Body = Parameters<typeof twoFactor>[0];

/** The API's 400 (Identity's "InvalidTwoFactorCode") in the owner's words; 429 = the per-user limit of /manage (R11 B-Med2). */
function message(error: ApiError, weakening: boolean) {
  if (error.status === 400)
    return weakening
      ? "El código no es válido. Use el de 6 dígitos de la aplicación o un código de respaldo que no haya usado."
      : "El código no es válido. Revise que la hora del teléfono esté en automático e intente de nuevo.";
  // R12 #2: too many wrong codes locks it for a while (the API's own words); otherwise the per-minute limit.
  if (error.status === 429) return error.code === "Auth.TooManyCodes" ? error.detail : "Demasiados intentos seguidos. Espere un minuto e intente de nuevo.";
  return error.detail;
}

/** R11 B-Med6: "Seguridad", the account's two-step verification (Identity's /manage/2fa). Required for Abastalia staff in the platform console;
 * any owner may turn it on. TK-298: a QR and the key to type, backup codes shown once (copy, download), and turning it off, a new phone or new
 * backup codes need the current code (the API checks it too). Lost phone and codes: Abastalia staff run reset-2fa after checking who asks. */
export function SecurityPage() {
  const me = useApi<Me>("/api/me");
  const [status, setStatus] = useState<TwoFactorStatus | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<{ error: ApiError; weakening: boolean } | null>(null);
  const [busy, setBusy] = useState(false);
  const [signedOutElsewhere, setSignedOutElsewhere] = useState(false);

  /** `true` when it worked. */
  const run = async (body: Body = {}) => {
    const weakening = body.enable === false || !!body.resetSharedKey || !!body.resetRecoveryCodes;
    setBusy(true);
    setError(null);
    try {
      const next = await twoFactor(body);
      setStatus(next);
      if (next.recoveryCodes?.length) setCodes(next.recoveryCodes);
      // R12 #12: turning it on or off, or a new phone, closes every session (Identity's security stamp), this one too at its next renewal.
      if (body.enable !== undefined || body.resetSharedKey) setSignedOutElsewhere(true);
      return true;
    } catch (err) {
      setError({ error: err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."), weakening });
      return false;
    } finally {
      setBusy(false);
    }
  };

  // Once: a second call at the same time could create a second key (React runs effects twice in development).
  const started = useRef(false);
  useEffect(() => {
    if (!started.current) void run({});
    started.current = true;
  }, []);

  const failure = error && (error.error.status === 400 || error.error.status === 429 ? <Notice kind="error" title={message(error.error, error.weakening)} /> : <LoadError error={error.error} />);

  return (
    <>
      <PageHead title="Seguridad" subtitle="Verificación en dos pasos: además de la contraseña, un código de su teléfono al entrar." />
      {signedOutElsewhere && !codes && (
        <Notice kind="info" title="Por seguridad cerramos sus sesiones en los demás equipos.">
          Esta también se cerrará en unos minutos: termine aquí y vuelva a entrar.{" "}
          <button type="button" className="esc-button esc-button--small" onClick={signOut}>
            Entrar de nuevo ahora
          </button>
        </Notice>
      )}
      {!status && !error && <Skeleton height={160} />}
      {!status && failure}
      {codes && <BackupCodes codes={codes} email={me.data?.email ?? ""} onDone={() => setCodes(null)} />}
      {status && !codes && (
        <section className="esc-card esc-card--padded sec-card" aria-label="Verificación en dos pasos">
          {status.isTwoFactorEnabled ? (
            <Enabled status={status} busy={busy} failure={failure} run={run} />
          ) : (
            <Setup sharedKey={status.sharedKey} email={me.data?.email ?? ""} busy={busy} failure={failure} run={run} />
          )}
        </section>
      )}
    </>
  );
}

function Setup({ sharedKey, email, busy, failure, run }: { sharedKey: string; email: string; busy: boolean; failure: ReactNode; run: (b: Body) => Promise<boolean> }) {
  const [code, setCode] = useState("");
  const [copied, setCopied] = useState(false);
  const qr = `data:image/svg+xml;utf8,${encodeURIComponent(renderSVG(otpauthUri(sharedKey, email || "Panel"), { border: 2 }))}`;

  const enable = (e: FormEvent) => {
    e.preventDefault();
    void run({ enable: true, twoFactorCode: code.replace(/\s/g, "") }).then((ok) => ok && setCode(""));
  };
  async function copy() {
    try {
      await navigator.clipboard.writeText(sharedKey);
      setCopied(true);
    } catch {
      // No clipboard: the key stays on screen to type.
    }
  }

  return (
    <form className="sec-steps" onSubmit={enable}>
      <h2 className="sec-title">Actívela en tres pasos</h2>
      <ol>
        <li>
          <b>Instale una aplicación de autenticación</b> en su teléfono, por ejemplo Google Authenticator o Microsoft Authenticator.
        </li>
        <li>
          <b>En la aplicación, agregue una cuenta y escanee este código.</b>
          <div className="sec-qr">
            <img src={qr} width={176} height={176} alt="Código QR para agregar Abastalia a la aplicación de autenticación" />
            <div className="sec-key">
              <span className="esc-muted">¿No puede escanear? Escriba esta clave:</span>
              <code>{groups(sharedKey)}</code>
              <button type="button" className="esc-button esc-button--small" onClick={() => void copy()}>
                {copied ? "Clave copiada" : "Copiar clave"}
              </button>
            </div>
          </div>
        </li>
        <li>
          <label htmlFor="sec-code">
            <b>Escriba el código de 6 dígitos</b> que muestra la aplicación.
          </label>
          <div className="sec-row">
            <input
              id="sec-code"
              className="esc-input sec-code"
              inputMode="numeric"
              autoComplete="one-time-code"
              pattern="[0-9 ]{6,7}"
              placeholder="123456"
              required
              value={code}
              onChange={(e) => setCode(e.target.value)}
            />
            <button type="submit" className="esc-button esc-button--primary" disabled={busy}>
              {busy ? "Un momento…" : "Activar"}
            </button>
          </div>
        </li>
      </ol>
      {failure}
    </form>
  );
}

/** Shown once, right after turning it on or asking for new ones: the API never returns them again. */
function BackupCodes({ codes, email, onDone }: { codes: string[]; email: string; onDone: () => void }) {
  const [copied, setCopied] = useState(false);
  const [saved, setSaved] = useState(false);
  const text = `Códigos de respaldo de Abastalia${email ? ` (${email})` : ""}\nCada uno sirve una sola vez para entrar sin el teléfono.\n\n${codes.join("\n")}\n`;

  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
    } catch {
      // No clipboard: the codes stay on screen to write down or download.
    }
  }
  function download() {
    const url = URL.createObjectURL(new Blob([text], { type: "text/plain;charset=utf-8" }));
    const link = document.createElement("a");
    link.href = url;
    link.download = "abastalia-codigos-de-respaldo.txt";
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  return (
    <section className="esc-card esc-card--padded sec-card" aria-labelledby="sec-codes-title">
      <Notice kind="success" title="La verificación en dos pasos está activa." />
      <h2 id="sec-codes-title" className="sec-title">
        Guarde sus códigos de respaldo
      </h2>
      <Notice kind="warning" title="Solo se muestran esta vez.">
        Si pierde o cambia el teléfono, entre con uno de estos códigos en lugar del código de la aplicación. Cada uno sirve una vez. Guárdelos fuera
        del teléfono: en papel o en un lugar seguro.
      </Notice>
      <ul className="sec-codes" aria-label="Códigos de respaldo">
        {codes.map((c) => (
          <li key={c}>
            <code>{c}</code>
          </li>
        ))}
      </ul>
      <div className="sec-row">
        <button type="button" className="esc-button esc-button--small" onClick={() => void copy()}>
          {copied ? "Copiados" : "Copiar"}
        </button>
        <button type="button" className="esc-button esc-button--small" onClick={download}>
          Descargar .txt
        </button>
      </div>
      <label className="sec-check">
        <input type="checkbox" checked={saved} onChange={(e) => setSaved(e.target.checked)} /> Ya guardé los códigos
      </label>
      <div>
        <button type="button" className="esc-button esc-button--primary" disabled={!saved} onClick={onDone}>
          Listo
        </button>
      </div>
    </section>
  );
}

type Action = "phone" | "codes" | "off";
const actions: Record<Action, { label: string; body: Body; danger?: boolean }> = {
  phone: { label: "Cambiar de teléfono", body: { resetSharedKey: true } },
  codes: { label: "Nuevos códigos de respaldo", body: { resetRecoveryCodes: true } },
  off: { label: "Desactivar", body: { enable: false }, danger: true },
};

function Enabled({ status, busy, failure, run }: { status: TwoFactorStatus; busy: boolean; failure: ReactNode; run: (b: Body) => Promise<boolean> }) {
  const [action, setAction] = useState<Action | null>(null);
  const [code, setCode] = useState("");
  const left = status.recoveryCodesLeft;

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (action) void run({ ...actions[action].body, twoFactorCode: code.trim() }).then((ok) => ok && (setAction(null), setCode("")));
  };

  return (
    <div className="sec-steps">
      <Notice kind="success" title="La verificación en dos pasos está activa.">
        Al entrar le pedimos el código de su aplicación de autenticación.
      </Notice>
      <p className={left <= 3 ? "sec-left sec-left--low" : "sec-left"}>
        Le quedan <b>{left}</b> {left === 1 ? "código" : "códigos"} de respaldo.{left <= 3 && " Pida nuevos antes de quedarse sin ninguno."}
      </p>
      {action ? (
        <form className="sec-steps" onSubmit={submit}>
          <h2 className="sec-title">{actions[action].label}</h2>
          {/* R12 design review: the gap without a second step is the one thing to notice here, so it is a warning, not muted text. */}
          {action === "phone" ? (
            <Notice kind="warning" title="Tenga el teléfono nuevo a mano.">
              La aplicación del teléfono actual deja de servir. Hasta que escanee el código nuevo, en la pantalla que sigue, su cuenta queda sin
              verificación en dos pasos.
            </Notice>
          ) : (
            <p className="esc-muted">{action === "off" ? "Al entrar solo le pediremos la contraseña." : "Los códigos de respaldo anteriores dejan de servir."}</p>
          )}
          <label htmlFor="sec-confirm-code">Para confirmar, escriba el código de la aplicación o un código de respaldo</label>
          <div className="sec-row">
            <input
              id="sec-confirm-code"
              className="esc-input sec-code"
              autoComplete="one-time-code"
              autoCapitalize="characters"
              required
              autoFocus
              value={code}
              onChange={(e) => setCode(e.target.value)}
            />
            <button type="submit" className={`esc-button ${actions[action].danger ? "esc-button--danger" : "esc-button--primary"}`} disabled={busy}>
              {busy ? "Un momento…" : actions[action].label}
            </button>
            <button type="button" className="esc-button esc-button--ghost" onClick={() => (setAction(null), setCode(""))}>
              Cancelar
            </button>
          </div>
          {failure}
        </form>
      ) : (
        <div className="sec-row">
          {(Object.keys(actions) as Action[]).map((a) => (
            <button key={a} type="button" className={`esc-button esc-button--small${actions[a].danger ? " esc-button--danger" : ""}`} onClick={() => setAction(a)}>
              {actions[a].label}
            </button>
          ))}
        </div>
      )}
      <div className="sec-lost">
        <h3>¿Perdió el teléfono?</h3>
        <p>
          Entre con un código de respaldo y toque «Cambiar de teléfono». Si también perdió los códigos,{" "}
          <a href={contactHref}>{site.whatsapp ? "escríbanos por WhatsApp" : "escríbanos"}</a>: comprobamos que es usted y quitamos la verificación
          para que la active de nuevo.
        </p>
      </div>
    </div>
  );
}
