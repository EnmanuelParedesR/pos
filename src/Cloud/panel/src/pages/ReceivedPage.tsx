import { Fragment, useState, type FormEvent } from "react";
import { api, ApiError, useApi, type ReceivedEcf } from "../api";
import { usePanel } from "../App";
import { dateTime, dayName, money } from "../format";
import { EmptyState, LoadError, Notice, PageHead, SkeletonRows } from "../ui";
import { ReceiverPanel } from "./ReceiverPanel";

const environmentNames: Record<string, string> = { ecf: "Producción", certecf: "Certificación", testecf: "Pruebas" };

/** The DGII takes the rejection reason as free text (ACECF `DetalleMotivoRechazo`, up to 250 characters, no code list: §11 question 45).
 * These are only suggestions. */
const rejectReasons = ["Montos incorrectos", "Mercancía no recibida", "Mercancía dañada o incompleta", "Precio distinto al acordado", "Factura duplicada"];

/** "Recibidos" (TK-154): the e-CF suppliers sent to the business, with their commercial approval. TK-246: the approval is signed and sent at the store.
 * TK-250: an owner can decide it here; it waits for the store's main PC, which signs and sends it. */
export function ReceivedPage() {
  const { canEdit } = usePanel();
  const { data, error, loading, reload } = useApi<ReceivedEcf[]>("/api/panel/fe/received");
  const [open, setOpen] = useState<number | null>(null);

  return (
    <>
      <PageHead title="Comprobantes recibidos" subtitle="Las facturas electrónicas que le enviaron sus suplidores." />
      <ReceiverPanel canEdit={canEdit} />
      <LoadError error={error} />
      <section className="esc-card esc-table-wrap">
        <table className="esc-table esc-table--wide">
          <thead>
            <tr>
              <th>Suplidor</th>
              <th>e-NCF</th>
              <th className="esc-hide-sm">Fecha</th>
              <th className="esc-num">Total</th>
              <th>Aprobación</th>
            </tr>
          </thead>
          <tbody>
            {loading && !data && <SkeletonRows columns={5} />}
            {data?.map((d) => (
              <Fragment key={d.id}>
                <tr className="selectable" tabIndex={0} aria-expanded={open === d.id} onClick={() => setOpen(open === d.id ? null : d.id)} onKeyDown={(e) => e.key === "Enter" && setOpen(open === d.id ? null : d.id)}>
                  <td>
                    <b>{d.issuerName}</b>
                    <span className="esc-table__sub">RNC {d.issuerRnc}</span>
                  </td>
                  <td>
                    {d.eNcf}
                    {d.environment !== "ecf" && <span className="esc-table__sub">{environmentNames[d.environment] ?? d.environment}</span>}
                  </td>
                  <td className="esc-hide-sm">{dayName(d.issuedOn)}</td>
                  <td className="esc-num">{money(d.total)}</td>
                  <td>
                    <ApprovalPill doc={d} />
                  </td>
                </tr>
                {open === d.id && (
                  <tr>
                    <td colSpan={5}>
                      <Detail doc={d} canEdit={canEdit} onDecided={reload} />
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
        </table>
        {data?.length === 0 && (
          <EmptyState icon="inbox" title="Todavía no ha recibido comprobantes">
            Llegan aquí cuando un suplidor le envía una factura electrónica al RNC del negocio.
          </EmptyState>
        )}
      </section>
    </>
  );
}

function ApprovalPill({ doc }: { doc: ReceivedEcf }) {
  if (doc.signerUnverified) return <span className="esc-pill esc-pill--error">Firmante sin verificar</span>;
  if (doc.approval === "Approved") return <span className="esc-pill esc-pill--success">Aprobado</span>;
  if (doc.approval === "Rejected") return <span className="esc-pill esc-pill--error">Rechazado</span>;
  if (doc.panelDecision) return <span className="esc-pill esc-pill--warning">{doc.panelDecision === "Approved" ? "Aceptado" : "Rechazado"} · en espera</span>;
  return <span className="esc-pill esc-pill--warning">Pendiente</span>;
}

function Detail({ doc, canEdit, onDecided }: { doc: ReceivedEcf; canEdit: boolean; onDecided: () => void }) {
  const [error, setError] = useState<ApiError | null>(null);
  const [xml, setXml] = useState<string | null>(null);

  const showXml = () => api<{ xml: string }>(`/api/panel/fe/received/${doc.id}/xml`).then((r) => setXml(r.xml), (err) => setError(err as ApiError));
  const waiting = doc.approval === null && doc.panelDecision;

  return (
    <div className="esc-col" style={{ gap: 12 }}>
      <ul className="platform-kv">
        <li>
          <span>Tipo</span>
          <b>{doc.type}</b>
        </li>
        <li>
          <span>ITBIS</span>
          <b>{money(doc.itbis)}</b>
        </li>
        <li>
          <span>Recibido</span>
          <b>{dateTime(doc.receivedUtc)}</b>
        </li>
        {doc.approvalUtc && (
          <li>
            <span>{doc.approval === "Approved" ? "Aprobado" : "Rechazado"}</span>
            <b>
              {dateTime(doc.approvalUtc)}
              {doc.approvalReason && ` · ${doc.approvalReason}`}
            </b>
          </li>
        )}
        {doc.approval && (
          <li>
            <span>Enviado</span>
            <b>
              {[doc.approvalDeliveredUtc ? "al suplidor" : null, doc.approvalDgiiUtc ? "a la DGII" : null].filter(Boolean).join(" y ") || "todavía no"}
            </b>
          </li>
        )}
      </ul>
      {doc.approvalMessage && <Notice kind="warning" title={doc.approvalMessage} />}
      {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}

      {doc.acknowledgedUtc === null && (
        <Notice kind="warning" title="Acuse de recibo pendiente">
          Llegó con la PC de la tienda apagada. El acuse se firma cuando la PC vuelve a conectarse.
        </Notice>
      )}
      {/* TK-246/TK-250: the approval is signed with the store's certificate and sent from the store, whoever decided it. */}
      {waiting && (
        <Notice kind="info" title="Pendiente: se firmará en la tienda">
          {doc.panelDecision === "Approved" ? "Aceptado" : `Rechazado (${doc.panelReason})`} por {doc.panelDecidedBy}
          {doc.panelDecidedUtc && ` el ${dateTime(doc.panelDecidedUtc)}`}. La PC principal de la tienda lo firma y lo envía al suplidor y a la DGII cuando
          esté encendida y conectada.
        </Notice>
      )}
      {doc.approval && doc.panelDecision && doc.panelDecision !== doc.approval && (
        <p className="esc-muted">Se decidió primero en la tienda: esa decisión es la que vale.</p>
      )}
      {/* R11 B-High2: a person's certificate (cédula) for the supplier's company: nobody here can verify that person signs for it. */}
      {doc.signerUnverified && (
        <Notice kind="error" title="Firmante sin verificar">
          Lo firmó una persona por la empresa del suplidor y no se puede verificar que firme por ella. No pasa a la tienda (ni a compras ni al 606):
          confírmelo con el suplidor.
        </Notice>
      )}
      {doc.approval === null && !waiting && !doc.signerUnverified &&
        (canEdit ? (
          <DecisionForm id={doc.id} onDecided={onDecided} />
        ) : (
          <p className="esc-muted">El dueño lo acepta o lo rechaza aquí o en la PC principal de la tienda (Compras → Recibidos).</p>
        ))}

      {xml === null ? (
        <div>
          <button type="button" className="esc-button esc-button--small esc-button--ghost" onClick={() => void showXml()}>
            Ver el XML
          </button>
        </div>
      ) : (
        <pre className="esc-code" style={{ whiteSpace: "pre-wrap", overflowWrap: "anywhere", maxHeight: 320, overflow: "auto" }}>
          {xml}
        </pre>
      )}
    </div>
  );
}

/** TK-250: Aceptar (one tap, then confirm) or Rechazar with its reason. The decision cannot change once made. */
function DecisionForm({ id, onDecided }: { id: number; onDecided: () => void }) {
  const [choice, setChoice] = useState<"accept" | "reject" | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    api(`/api/panel/fe/received/${id}/decision`, "POST", choice === "accept" ? { approve: true } : { approve: false, reason: reason.trim() }).then(
      () => onDecided(),
      (err) => (setError(err as ApiError), setBusy(false)),
    );
  };

  if (choice === null)
    return (
      <div className="esc-row" style={{ gap: 8 }}>
        <button type="button" className="esc-button esc-button--small esc-button--primary" onClick={() => setChoice("accept")}>
          Aceptar
        </button>
        <button type="button" className="esc-button esc-button--small esc-button--danger" onClick={() => setChoice("reject")}>
          Rechazar
        </button>
        <span className="esc-muted">Opcional. La tienda firma la decisión y la envía al suplidor y a la DGII.</span>
      </div>
    );
  return (
    <form className="esc-col" style={{ gap: 8 }} onSubmit={submit}>
      {choice === "reject" && (
        <label className="esc-field">
          <span>Motivo del rechazo</span>
          <input className="esc-input" list={`reasons-${id}`} required maxLength={250} value={reason} onChange={(e) => setReason(e.target.value)} />
          <datalist id={`reasons-${id}`}>
            {rejectReasons.map((r) => (
              <option key={r} value={r} />
            ))}
          </datalist>
        </label>
      )}
      <span className="esc-muted">No se puede cambiar después.</span>
      <div className="esc-row" style={{ gap: 8 }}>
        <button type="submit" className={`esc-button esc-button--small ${choice === "accept" ? "esc-button--primary" : "esc-button--danger"}`} disabled={busy}>
          {busy ? "Un momento…" : choice === "accept" ? "Sí, aceptar" : "Sí, rechazar"}
        </button>
        <button type="button" className="esc-button esc-button--small esc-button--ghost" onClick={() => (setChoice(null), setError(null))}>
          Cancelar
        </button>
      </div>
      {error && <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />}
    </form>
  );
}
