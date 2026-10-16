import { useState } from "react";
import { api, ApiError, useApi, type ReceiverPanel as ReceiverData } from "../api";
import { dateTime } from "../format";
import { LoadError, Notice } from "../ui";

const environmentNames: Record<string, string> = { ecf: "Producción", certecf: "Certificación", testecf: "Pruebas" };

/** TK-241: the cloud receiver (TK-151): who signs (TK-246: the store's PC or a certificate copy), the hours it was without its store, the
 * addresses to register in the DGII directory, the latest signatures and the approvals buyers sent. Only an Owner withdraws the consent, after
 * confirming twice. */
export function ReceiverPanel({ canEdit }: { canEdit: boolean }) {
  const { data, error, reload } = useApi<ReceiverData>("/api/panel/fe");
  const s = data?.status;

  return (
    <section className="esc-card esc-card--padded esc-col" style={{ gap: 12, marginBottom: 16 }}>
      <h2>Receptor en la nube</h2>
      <LoadError error={error} />
      {s && !s.active && (
        <p className="esc-muted">
          No está activo. Se activa en la PC principal de la tienda, con el certificado digital del negocio.
        </p>
      )}
      {data && s?.active && (
        <>
          <ul className="platform-kv">
            <li>
              <span>RNC</span>
              <b>{s.rnc}</b>
            </li>
            <li>
              <span>Firma</span>
              <b>{s.signingMode === "CloudCustody" ? "Copia del certificado en la nube" : "En la PC de la tienda (el certificado no sale de la tienda)"}</b>
            </li>
            <li>
              <span>Certificado</span>
              <b>{s.subject}</b>
            </li>
            {s.notAfterUtc && (
              <li>
                <span>Vence</span>
                <b>{dateTime(s.notAfterUtc)}</b>
              </li>
            )}
            {s.consentedUtc && (
              <li>
                <span>Consentimiento</span>
                <b>
                  {dateTime(s.consentedUtc)} · dueño {s.ownerName}, titular {s.holderName}
                </b>
              </li>
            )}
          </ul>
          {data.offline?.map((o) => (
            <Notice key={o.node} kind="warning" title={o.offlineSinceUtc ? `Receptor sin conexión: ${o.node} no responde desde ${dateTime(o.offlineSinceUtc)}` : `Receptor sin conexión: ${o.hours} h en 30 días (${o.node})`}>
              Mientras la PC de la tienda está apagada, los suplidores reciben "intente de nuevo" y el acuse se firma cuando vuelve.
            </Notice>
          ))}
          <details>
            <summary>Direcciones para el directorio de la DGII</summary>
            <p className="esc-muted">Cópielas completas en la OFV, "Mantenimiento Directorio FE", para cada ambiente.</p>
            {s.urls?.map((u) => (
              <ul key={u.environment} className="platform-kv">
                <li>
                  <span>Ambiente</span>
                  <b>{environmentNames[u.environment] ?? u.environment}</b>
                </li>
                <li>
                  <span>Recepción</span>
                  <code style={{ overflowWrap: "anywhere" }}>{u.reception}</code>
                </li>
                <li>
                  <span>Aprobación comercial</span>
                  <code style={{ overflowWrap: "anywhere" }}>{u.approval}</code>
                </li>
                <li>
                  <span>Autenticación</span>
                  <code style={{ overflowWrap: "anywhere" }}>{u.authentication}</code>
                </li>
              </ul>
            ))}
          </details>
          <details>
            <summary>Últimas firmas del receptor ({data.signatures.length})</summary>
            {data.signatures.length === 0 ? (
              <p className="esc-muted">Todavía no hay firmas.</p>
            ) : (
              <ul className="platform-kv">
                {data.signatures.slice(0, 20).map((g) => (
                  <li key={`${g.atUtc}${g.sha256}`}>
                    <span>{dateTime(g.atUtc)}</span>
                    <b>
                      {g.root}
                      {g.eNcf && ` · ${g.eNcf}`}
                    </b>
                  </li>
                ))}
              </ul>
            )}
          </details>
          <details>
            <summary>Aprobaciones recibidas de compradores ({data.approvals.length})</summary>
            {data.approvals.length === 0 ? (
              <p className="esc-muted">Ningún comprador ha enviado aprobaciones.</p>
            ) : (
              <ul className="platform-kv">
                {data.approvals.slice(0, 20).map((a) => (
                  <li key={`${a.eNcf}${a.receivedUtc}`}>
                    <span>{dateTime(a.receivedUtc)}</span>
                    <b>
                      {a.eNcf} · RNC {a.buyerRnc} · {a.approved ? "Aprobado" : `Rechazado${a.reason ? `: ${a.reason}` : ""}`}
                    </b>
                  </li>
                ))}
              </ul>
            )}
          </details>
          {canEdit && <Revoke onDone={reload} />}
        </>
      )}
    </section>
  );
}

/** Two confirmations: what it does, then the last word. */
function Revoke({ onDone }: { onDone: () => void }) {
  const [step, setStep] = useState(0);
  const [error, setError] = useState<ApiError | null>(null);
  const revoke = () =>
    api("/api/panel/fe/revoke", "POST").then(
      () => (setStep(0), onDone()),
      (e) => setError(e as ApiError),
    );

  if (step === 0)
    return (
      <div>
        <button type="button" className="esc-button esc-button--small esc-button--danger" onClick={() => setStep(1)}>
          Retirar
        </button>
      </div>
    );
  return (
    <div className="esc-col" style={{ gap: 8 }}>
      <Notice kind="warning" title="El receptor deja de responder (y si hay copia del certificado en la nube, se borra).">

        Los suplidores no podrán enviarle comprobantes hasta que lo active otra vez desde la tienda.
      </Notice>
      {error && <Notice kind="error" title={error.detail} />}
      <div className="esc-form-actions">
        {step === 1 ? (
          <button type="button" className="esc-button esc-button--danger" onClick={() => setStep(2)}>
            Entiendo, continuar
          </button>
        ) : (
          <button type="button" className="esc-button esc-button--danger" onClick={() => void revoke()}>
            Sí, retirar el receptor
          </button>
        )}
        <button type="button" className="esc-button esc-button--ghost" onClick={() => (setStep(0), setError(null))}>
          Cancelar
        </button>
      </div>
    </div>
  );
}
