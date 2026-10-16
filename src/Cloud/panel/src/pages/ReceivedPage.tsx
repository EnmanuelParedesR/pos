import { Fragment, useState } from "react";
import { api, ApiError, useApi, type ReceivedEcf } from "../api";
import { usePanel } from "../App";
import { dateTime, dayName, money } from "../format";
import { EmptyState, Icon, LoadError, Notice, PageHead, SkeletonRows } from "../ui";

const environmentNames: Record<string, string> = { ecf: "Producción", certecf: "Certificación", testecf: "Pruebas" };

/** "Recibidos" (TK-154): the e-CF suppliers sent to the business, with their commercial approval. Only an Owner approves or rejects;
 * the cloud signs the approval and sends it to the supplier and to the DGII. */
export function ReceivedPage() {
  const { canEdit } = usePanel();
  const { data, error, loading, reload } = useApi<ReceivedEcf[]>("/api/panel/fe/received");
  const [open, setOpen] = useState<number | null>(null);

  return (
    <>
      <PageHead title="Comprobantes recibidos" subtitle="Las facturas electrónicas que le enviaron sus suplidores." />
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
                    <ApprovalPill approval={d.approval} />
                  </td>
                </tr>
                {open === d.id && (
                  <tr>
                    <td colSpan={5}>
                      <Detail doc={d} canEdit={canEdit} onChange={reload} />
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

function ApprovalPill({ approval }: { approval: ReceivedEcf["approval"] }) {
  if (approval === "Approved") return <span className="esc-pill esc-pill--success">Aprobado</span>;
  if (approval === "Rejected") return <span className="esc-pill esc-pill--error">Rechazado</span>;
  return <span className="esc-pill esc-pill--warning">Pendiente</span>;
}

function Detail({ doc, canEdit, onChange }: { doc: ReceivedEcf; canEdit: boolean; onChange: () => void }) {
  const [reason, setReason] = useState("");
  const [rejecting, setRejecting] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<ApiError | null>(null);
  const [xml, setXml] = useState<string | null>(null);

  const decide = async (approve: boolean, why = reason.trim()) => {
    setBusy(true);
    setError(null);
    try {
      await api(`/api/panel/fe/received/${doc.id}/approval`, "POST", approve ? { approve } : { approve, reason: why });
      onChange();
    } catch (err) {
      setError(err as ApiError);
    } finally {
      setBusy(false);
    }
  };

  const showXml = () => api<{ xml: string }>(`/api/panel/fe/received/${doc.id}/xml`).then((r) => setXml(r.xml), (err) => setError(err as ApiError));
  // A decision already signed is sent again only where it did not arrive (the supplier or the DGII).
  const pending = doc.approval !== null && (doc.approvalDeliveredUtc === null || doc.approvalDgiiUtc === null);

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

      {canEdit && doc.approval === null && !rejecting && (
        <div className="esc-form-actions">
          <button type="button" className="esc-button esc-button--primary" disabled={busy} onClick={() => void decide(true)}>
            <Icon name="check" size={18} /> Aprobar
          </button>
          <button type="button" className="esc-button" disabled={busy} onClick={() => setRejecting(true)}>
            Rechazar
          </button>
        </div>
      )}
      {canEdit && doc.approval === null && rejecting && (
        <div className="esc-col" style={{ gap: 8 }}>
          <label className="esc-field">
            <span>Motivo del rechazo</span>
            <input className="esc-input" maxLength={250} value={reason} onChange={(e) => setReason(e.target.value)} />
          </label>
          <div className="esc-form-actions">
            <button type="button" className="esc-button esc-button--danger" disabled={busy || reason.trim() === ""} onClick={() => void decide(false)}>
              Rechazar comprobante
            </button>
            <button type="button" className="esc-button" disabled={busy} onClick={() => setRejecting(false)}>
              Cancelar
            </button>
          </div>
        </div>
      )}
      {canEdit && pending && (
        <div className="esc-form-actions">
          <button type="button" className="esc-button" disabled={busy} onClick={() => void decide(doc.approval === "Approved", doc.approvalReason ?? "")}>
            Reintentar el envío
          </button>
        </div>
      )}
      {!canEdit && doc.approval === null && <p className="esc-muted">Solo un dueño puede aprobar o rechazar este comprobante.</p>}

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
