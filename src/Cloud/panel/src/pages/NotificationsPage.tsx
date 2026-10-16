import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api, ApiError, apiUrl, useApi, type AlertChoice, type AlertPreferences } from "../api";
import { LoadError, Logo, Notice, PageHead, Skeleton } from "../ui";

const choices: [keyof AlertChoice, string, string][] = [
  ["dgiiRejected", "Comprobantes rechazados por la DGII", "Cuando la DGII rechaza un e-CF y todavía no tiene reemplazo."],
  ["queueStale", "Comprobantes sin enviar", "Cuando hay e-CF esperando más de 24 horas para llegar a la DGII (el plazo es de 72)."],
  ["certificateExpiring", "Certificado digital por vencer", "30 días antes de que venza el certificado con el que la tienda firma."],
  ["certificationProgress", "Avance de la certificación DGII", "Cada día que la tienda marca un paso de la certificación."],
];

/** "Avisos" (TK-222): each member chooses which alerts reach their e-mail. At most one of each kind per store and day. */
export function NotificationsPage() {
  const { data, error, loading } = useApi<AlertPreferences>("/api/notifications");
  const [prefs, setPrefs] = useState<AlertPreferences | null>(null);
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  useEffect(() => setPrefs(data ?? null), [data]);

  const save = (change: Partial<AlertChoice>) => {
    if (!prefs) return;
    // After unsubscribing, only what the member ticks now comes back.
    const base = prefs.unsubscribed ? { dgiiRejected: false, certificateExpiring: false, queueStale: false, certificationProgress: false } : {};
    const next = { ...prefs, ...base, ...change, unsubscribed: false };
    setPrefs(next);
    const { dgiiRejected, certificateExpiring, queueStale, certificationProgress } = next;
    api<AlertPreferences>("/api/notifications", "PUT", { dgiiRejected, certificateExpiring, queueStale, certificationProgress }).then(
      (saved) => (setSaveError(null), setPrefs(saved)),
      (err) => setSaveError(err as ApiError),
    );
  };

  return (
    <>
      <PageHead title="Avisos" subtitle={prefs?.email ? `Le escribimos a ${prefs.email} cuando algo de la tienda necesita su atención.` : undefined} />
      <LoadError error={error} />
      {loading && !prefs && <Skeleton height={160} />}
      {prefs && (
        <section className="esc-card esc-card--padded" aria-label="Avisos por correo">
          {!prefs.emailDelivers && <Notice kind="info" title="Todavía no enviamos correos.">Sus elecciones quedan guardadas para cuando empecemos.</Notice>}
          {prefs.unsubscribed && <Notice kind="warning" title="Usted se dio de baja de todos los avisos.">Marque los que quiera recibir otra vez.</Notice>}
          {saveError && <Notice kind={saveError.offline ? "warning" : "error"} title={saveError.detail} />}
          <ul className="platform-kv">
            {choices.map(([key, label, hint]) => (
              <li key={key}>
                <label className="esc-row" style={{ gap: 10, alignItems: "flex-start" }}>
                  <input type="checkbox" checked={!prefs.unsubscribed && prefs[key]} onChange={(e) => save({ [key]: e.target.checked })} />
                  <span className="esc-col" style={{ gap: 2 }}>
                    <b>{label}</b>
                    <span className="esc-muted">{hint}</span>
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </section>
      )}
    </>
  );
}

/** The link at the end of every alert e-mail: no session. R8 #12: the token is after "#", never sent in a URL. */
export function UnsubscribePage() {
  const [state, setState] = useState<"working" | "done" | "invalid" | "offline">("working");

  useEffect(() => {
    const token = window.location.hash.slice(1);
    fetch(`${apiUrl}/api/notifications/unsubscribe`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ token }) }).then(
      (r) => setState(r.ok ? "done" : "invalid"),
      () => setState("offline"),
    );
  }, []);

  return (
    <div className="esc-admin" style={{ minHeight: "100vh", display: "grid", placeItems: "center", padding: 16 }}>
      <section className="esc-card esc-card--padded" style={{ maxWidth: 420 }}>
        <Logo size={32} />
        {state === "working" && <p>Un momento…</p>}
        {state === "done" && (
          <Notice kind="success" title="Listo: ya no le enviaremos avisos.">
            Puede volver a activarlos en <Link to="/app/notifications">Avisos</Link>, dentro del panel.
          </Notice>
        )}
        {state === "invalid" && <Notice kind="error" title="El enlace no es válido.">Cambie sus avisos desde el panel, en Avisos.</Notice>}
        {state === "offline" && <Notice kind="warning" title="No hay conexión con el servidor.">Revise el internet e intente de nuevo.</Notice>}
      </section>
    </div>
  );
}
