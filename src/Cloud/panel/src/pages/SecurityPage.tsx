import { useEffect, useRef, useState, type FormEvent } from "react";
import { ApiError, twoFactor, type TwoFactorStatus } from "../api";
import { ConfirmButton, LoadError, Notice, PageHead, Skeleton } from "../ui";

/** The key in groups of four, as authenticator apps show it. */
const groups = (key: string) => key.replace(/(.{4})/g, "$1 ").trim();

/** R11 B-Med6: "Seguridad", the account's two-step verification (Identity's /manage/2fa). Required for Toki staff in the platform console;
 * any owner may turn it on. The key goes in an authenticator app (Google Authenticator, Microsoft Authenticator…); the backup codes show once. */
export function SecurityPage() {
  const [status, setStatus] = useState<TwoFactorStatus | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);

  const run = async (body: Parameters<typeof twoFactor>[0]) => {
    setBusy(true);
    setError(null);
    try {
      setStatus(await twoFactor(body));
      setCode("");
    } catch (err) {
      setError(err instanceof ApiError ? err : new ApiError(0, "Unknown", "No se pudo completar la operación."));
    }
    setBusy(false);
  };

  // Once: a second call at the same time could create a second key (React runs effects twice in development).
  const started = useRef(false);
  useEffect(() => {
    if (!started.current) void run({});
    started.current = true;
  }, []);

  const enable = (e: FormEvent) => {
    e.preventDefault();
    void run({ enable: true, twoFactorCode: code.replace(/\s/g, "") });
  };

  return (
    <>
      <PageHead title="Seguridad" subtitle="Verificación en dos pasos: además de la contraseña, un código de su teléfono al entrar." />
      {error && (error.status === 400 ? <Notice kind="error" title="El código no es válido. Revise la hora del teléfono e intente de nuevo." /> : <LoadError error={error} />)}
      {!status && !error && <Skeleton height={160} />}
      {status && (
        <section className="esc-card esc-card--padded" aria-label="Verificación en dos pasos">
          {status.recoveryCodes && status.recoveryCodes.length > 0 && (
            <Notice kind="warning" title="Guarde estos códigos de respaldo: no se vuelven a mostrar.">
              Cada uno sirve una vez para entrar si pierde el teléfono: <code>{status.recoveryCodes.join("  ")}</code>
            </Notice>
          )}
          {status.isTwoFactorEnabled ? (
            <>
              <Notice kind="success" title="La verificación en dos pasos está activa.">
                Le quedan {status.recoveryCodesLeft} códigos de respaldo.
              </Notice>
              <div className="esc-row" style={{ gap: 8 }}>
                <ConfirmButton label="Nuevos códigos de respaldo" confirm="Los anteriores dejan de servir." onConfirm={() => run({ resetRecoveryCodes: true })} />
                <ConfirmButton label="Desactivar" danger confirm="¿Desactivar la verificación en dos pasos?" onConfirm={() => run({ enable: false })} />
              </div>
            </>
          ) : (
            <form className="esc-col" style={{ gap: 12 }} onSubmit={enable}>
              <p>
                1. En su aplicación de autenticación, agregue una cuenta con esta clave: <code>{groups(status.sharedKey)}</code>
              </p>
              <label className="esc-col" style={{ gap: 4 }}>
                2. Escriba el código de 6 dígitos que muestra:
                <input
                  className="esc-input"
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  pattern="[0-9 ]{6,7}"
                  required
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                />
              </label>
              <div>
                <button type="submit" className="esc-button esc-button--primary" disabled={busy}>
                  {busy ? "Un momento…" : "Activar"}
                </button>
              </div>
            </form>
          )}
        </section>
      )}
    </>
  );
}
