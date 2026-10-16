import { useState } from "react";
import { api, ApiError, priceLabel, useApi, type PlatformReferrals, type ReferralReward, type ReferralRow, type ReferralStatus, type Referrals } from "../api";
import { dayName } from "../format";
import { Card, LoadError, Notice } from "../ui";

const day = (utc: string | null) => (utc ? dayName(utc.slice(0, 10)) : "—");

/** "10 %" or "US$5.00": the reward as the owner set it. */
export const rewardLabel = (r: ReferralReward) =>
  r.type === "percentage" ? `${r.amount} %` : priceLabel({ priceId: "", interval: null, amount: r.amount, currency: r.currency });

/** TK-255 "Recomiende Toki" (Owners): the link to share, how many referrals paid and the discount on the next payment. Hidden while it is off. */
export function ReferralCard() {
  const { data, error } = useApi<Referrals>("/api/billing/referrals");
  const [copied, setCopied] = useState(false);
  if (error) return <LoadError error={error} />;
  if (!data?.enabled || !data.code || !data.reward) return null;
  const link = `${window.location.origin}/precios?ref=${data.code}`;
  const next = data.nextDiscount;

  async function copy() {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
    } catch {
      setCopied(false); // no clipboard: the link is on screen to copy by hand
    }
  }

  return (
    <Card title="Recomiende Toki" aside={`${rewardLabel(data.reward)} en su próximo pago`}>
      <p className="esc-muted" style={{ marginTop: 0 }}>
        Comparta su enlace. Cuando un negocio que usted recomendó pague su plan, le descontamos {rewardLabel(data.reward)} en su próximo pago.
      </p>
      <div className="esc-row" style={{ gap: 12, flexWrap: "wrap" }}>
        <input className="esc-input" readOnly value={link} aria-label="Su enlace" style={{ flex: 1, minWidth: 240 }} onFocus={(e) => e.target.select()} />
        <button type="button" className="esc-button" onClick={copy}>
          {copied ? "Copiado" : "Copiar enlace"}
        </button>
      </div>
      <ul className="platform-kv" aria-label="Sus recomendaciones" style={{ marginTop: 12 }}>
        <li>
          <span>Pagaron y esperan su descuento</span>
          <b>{data.pending}</b>
        </li>
        <li>
          <span>Descuentos aplicados</span>
          <b>{data.applied}</b>
        </li>
        <li>
          <span>Próximo descuento</span>
          <span>
            {!next ? "—" : next.fromUtc ? `${rewardLabel(next)} en su pago del ${day(next.fromUtc)}` : `${rewardLabel(next)} en espera: se aplica en su próximo cobro`}
          </span>
        </li>
      </ul>
    </Card>
  );
}

export const referralStatusNames: Record<ReferralStatus, string> = { Pending: "Pendiente", Applied: "Aplicado", Reverted: "Revertido", Rejected: "Rechazado" };

const reasonNames: Record<string, string> = {
  SelfReferral: "El mismo negocio",
  SamePayer: "Mismo pagador",
  SameRnc: "Mismo RNC",
  MonthlyLimit: "Límite del mes",
  Refund: "Reembolso",
  Staff: "Ajuste de Toki",
};

/** Toki staff (TK-255): the business's referrals, made and received, with manual adjustments (a reason is required; the call is audited). */
export function PlatformReferralsCard({ tenantId }: { tenantId: string }) {
  const { data, error, reload } = useApi<PlatformReferrals>(`/api/platform/tenants/${tenantId}/referrals`);
  const [note, setNote] = useState("");
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  if (error) return <LoadError error={error} />;
  if (!data) return null;

  async function adjust(row: ReferralRow, action: "Revert" | "Reinstate" | "Retry") {
    try {
      await api(`/api/platform/tenants/${tenantId}/referrals/${row.id}`, "POST", { action, note: action === "Retry" ? null : note });
      setSaveError(null);
      setNote("");
      reload();
    } catch (err) {
      setSaveError(err as ApiError);
    }
  }

  return (
    <Card title="Recomendaciones" aside={data.code ? `Código ${data.code}` : "Sin código"}>
      {data.referredByCode && <p className="esc-muted">Llegó con el código {data.referredByCode}.</p>}
      {data.referrals.length === 0 ? (
        <p className="esc-muted">Sin recomendaciones.</p>
      ) : (
        <>
          <ul className="platform-kv" aria-label="Recomendaciones">
            {data.referrals.map((r) => (
              <li key={r.id}>
                <span>
                  {r.referrerTenantId === tenantId ? `Recomendó a ${r.referredName}` : `Recomendado por ${r.referrerName}`}
                  <span className="esc-muted">
                    {" · "}
                    {day(r.createdUtc)}
                    {r.discountFromUtc && ` · descuento en el pago del ${day(r.discountFromUtc)}`}
                    {r.reason && ` · ${reasonNames[r.reason] ?? r.reason}`}
                    {r.note && ` · ${r.note}`}
                  </span>
                </span>
                <span className="esc-row" style={{ gap: 8 }}>
                  <span className={`esc-pill esc-pill--${r.status === "Applied" ? "success" : r.status === "Pending" ? "warning" : "neutral"}`}>{referralStatusNames[r.status]}</span>
                  {(r.status === "Pending" || r.status === "Applied") && (
                    <button type="button" className="esc-button" disabled={!note.trim()} onClick={() => adjust(r, "Revert")}>
                      Revertir
                    </button>
                  )}
                  {(r.status === "Rejected" || r.status === "Reverted") && (
                    <button type="button" className="esc-button" disabled={!note.trim()} onClick={() => adjust(r, "Reinstate")}>
                      Restablecer
                    </button>
                  )}
                  {r.status === "Pending" && (
                    <button type="button" className="esc-button" onClick={() => adjust(r, "Retry")}>
                      Aplicar ahora
                    </button>
                  )}
                </span>
              </li>
            ))}
          </ul>
          <label className="esc-field" style={{ marginTop: 12 }}>
            <span>Motivo del ajuste</span>
            <input className="esc-input" value={note} maxLength={500} placeholder="Reembolso acordado, error nuestro…" onChange={(e) => setNote(e.target.value)} />
          </label>
        </>
      )}
      {saveError && <Notice kind="error" title={saveError.detail} />}
    </Card>
  );
}
