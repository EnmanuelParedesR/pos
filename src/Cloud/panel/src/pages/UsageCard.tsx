import { useEffect, useState } from "react";
import { api, ApiError, useApi, type Meter, type PlatformUsage, type Usage } from "../api";
import { dayName } from "../format";
import { Card, LoadError, Notice } from "../ui";

const count = (n: number) => n.toLocaleString("en-US");

const meterNames: Record<Meter, string> = {
  AssistantQuestions: "Preguntas al asistente",
  AssistantTokens: "Uso del asistente (tokens)",
  SupplierInvoices: "Facturas leídas con IA",
  Emails: "Correos enviados",
  ArchiveRetrievals: "Consultas al archivo de comprobantes",
  AccompanimentRequests: "Solicitudes de acompañamiento",
  Invites: "Invitaciones",
};

export const planNames: Record<string, string> = { Default: "Una caja", MultiRegister: "Varias cajas", MultiStore: "Varias sucursales" };
export const addOnNames: Record<string, string> = { SupplierInvoiceReading: "Lectura de facturas con IA (adicional)" };

/** The meters with use against limit; a meter at 90 % or more is marked. */
function UsageList({ usage }: { usage: Usage }) {
  return (
    <ul className="platform-kv" aria-label="Consumo del mes">
      {usage.meters.map((m) => {
        const share = m.limit > 0 ? Math.min(m.used / m.limit, 1) : 1;
        return (
          <li key={m.meter}>
            <span>{meterNames[m.meter] ?? m.meter}</span>
            <span className="esc-col" style={{ alignItems: "flex-end", gap: 0 }}>
              <span className={share >= 0.9 ? "esc-pill esc-pill--warning" : "esc-muted"}>
                {`${count(m.used)} de ${count(m.limit)}`}
              </span>
              <span className="platform-meter" style={{ width: 120 }} aria-hidden>
                <div style={{ width: `${share * 100}%` }} />
              </span>
            </span>
          </li>
        );
      })}
    </ul>
  );
}

const subtitle = (usage: Usage) =>
  `Plan ${planNames[usage.plan] ?? usage.plan}${usage.addOns.length ? ` + ${usage.addOns.map((a) => addOnNames[a] ?? a).join(", ")}` : ""} · se renueva el ${dayName(usage.resetsOn)}`;

/** "Consumo" (TK-248, Owners): what the business used this month of the services with a monthly limit. */
export function UsageCard() {
  const { data, error } = useApi<Usage>("/api/usage");
  if (error) return <LoadError error={error} />;
  if (!data?.meters) return null;
  return (
    <Card title="Consumo del mes" aside={subtitle(data)}>
      <UsageList usage={data} />
      {!data.addOns.includes("SupplierInvoiceReading") && (
        <p className="esc-muted">¿Lee muchas facturas de suplidores? Pida a Toki el servicio adicional de lectura de facturas con IA.</p>
      )}
    </Card>
  );
}

/** Toki staff (TK-248): the business's usage, and its plan and add-ons, which set its limits at once. */
export function PlatformUsageCard({ tenantId }: { tenantId: string }) {
  const { data, error, reload } = useApi<PlatformUsage>(`/api/platform/tenants/${tenantId}/usage`);
  const [plan, setPlan] = useState("");
  const [addOns, setAddOns] = useState<string[]>([]);
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  useEffect(() => {
    if (!data?.usage) return;
    setPlan(data.usage.plan);
    setAddOns(data.usage.addOns);
  }, [data]);

  if (error) return <LoadError error={error} />;
  if (!data?.usage) return null;
  const changed = plan !== data.usage.plan || addOns.join() !== data.usage.addOns.join();

  async function save() {
    try {
      await api(`/api/platform/tenants/${tenantId}/plan`, "PUT", { plan, addOns });
      setSaveError(null);
      reload();
    } catch (err) {
      setSaveError(err as ApiError);
    }
  }

  return (
    <Card title="Plan y consumo" aside={subtitle(data.usage)}>
      <div className="esc-row" style={{ gap: 12, flexWrap: "wrap", marginBottom: 12 }}>
        <label className="esc-field" style={{ minWidth: 200 }}>
          <span>Plan</span>
          <select className="esc-select" value={plan} onChange={(e) => setPlan(e.target.value)}>
            {data.plans.map((p) => (
              <option key={p} value={p}>
                {planNames[p] ?? p}
              </option>
            ))}
          </select>
        </label>
        {data.addOns.map((a) => (
          <label key={a} className="esc-row" style={{ gap: 6 }}>
            <input type="checkbox" checked={addOns.includes(a)} onChange={(e) => setAddOns(e.target.checked ? [...addOns, a] : addOns.filter((x) => x !== a))} />
            {addOnNames[a] ?? a}
          </label>
        ))}
        <button type="button" className="esc-button esc-button--primary" disabled={!changed} onClick={save}>
          Guardar plan
        </button>
      </div>
      {saveError && <Notice kind="error" title={saveError.detail} />}
      <UsageList usage={data.usage} />
    </Card>
  );
}
