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
  ReceiverSignatures: "Acuses firmados del receptor e-CF",
};

export const planNames: Record<string, string> = { Essential: "Esencial", Default: "Estándar", MultiRegister: "Pro", MultiStore: "Varias sucursales" };
export const addOnNames: Record<string, string> = {
  ExtraRegister: "Caja adicional",
  ExtraCashier: "Cajero adicional",
  ExtraStore: "Sucursal adicional",
  SupplierInvoiceReading: "Lectura de facturas con IA",
  AssistantPack: "Asistente +1,000 preguntas",
  ExtraUsers: "Usuarios del panel +5",
};

/** TK-258: add-ons come one entry per unit (["ExtraRegister", "ExtraRegister"] = two extra registers). */
export const countOf = (addOns: string[], code: string) => addOns.filter((a) => a === code).length;
export const withCount = (addOns: string[], code: string, n: number) =>
  [...addOns.filter((a) => a !== code), ...Array<string>(Math.max(0, Math.min(n || 0, 50))).fill(code)].sort();
/** "Caja adicional ×2, Lectura de facturas con IA". */
export const addOnList = (addOns: string[]) =>
  [...new Set(addOns)].map((a) => `${addOnNames[a] ?? a}${countOf(addOns, a) > 1 ? ` ×${countOf(addOns, a)}` : ""}`).join(", ");

/** A number per add-on (0 = not bought): the owner in "Plan y facturación" and Abastalia staff in the console. */
export function AddOnQuantities({ codes, value, onChange, note }: { codes: string[]; value: string[]; onChange: (addOns: string[]) => void; note?: (code: string) => string | null }) {
  // The order of addOnNames (registers, cashiers, stores first), as on /precios; unknown codes last.
  const order = (code: string) => (code in addOnNames ? Object.keys(addOnNames).indexOf(code) : 99);
  return (
    <>
      {[...codes].sort((a, b) => order(a) - order(b)).map((code) => (
        <label key={code} className="esc-field" style={{ minWidth: 160 }}>
          <span>
            {addOnNames[code] ?? code}
            {note?.(code) && <span className="esc-muted"> · {note(code)}</span>}
          </span>
          <input className="esc-input" type="number" min={0} max={50} value={countOf(value, code)} onChange={(e) => onChange(withCount(value, code, Number(e.target.value)))} />
        </label>
      ))}
    </>
  );
}

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
  `Plan ${planNames[usage.plan] ?? usage.plan}${usage.addOns.length ? ` + ${addOnList(usage.addOns)}` : ""} · se renueva el ${dayName(usage.resetsOn)}`;

/** "Consumo" (TK-248, Owners): what the business used this month of the services with a monthly limit. */
export function UsageCard() {
  const { data, error } = useApi<Usage>("/api/usage");
  if (error) return <LoadError error={error} />;
  if (!data?.meters) return null;
  return (
    <Card title="Consumo del mes" aside={subtitle(data)}>
      <UsageList usage={data} />
      {!data.addOns.includes("SupplierInvoiceReading") && (
        <p className="esc-muted">¿Lee muchas facturas de suplidores? Agregue la lectura de facturas con IA en Plan y facturación.</p>
      )}
    </Card>
  );
}

/** Abastalia staff (TK-248): the business's usage, and its plan and add-ons, which set its limits at once. */
export function PlatformUsageCard({ tenantId }: { tenantId: string }) {
  const { data, error, reload } = useApi<PlatformUsage>(`/api/platform/tenants/${tenantId}/usage`);
  const [plan, setPlan] = useState("");
  const [addOns, setAddOns] = useState<string[]>([]);
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  useEffect(() => {
    if (!data?.usage) return;
    setPlan(data.usage.plan);
    setAddOns([...data.usage.addOns].sort());
  }, [data]);

  if (error) return <LoadError error={error} />;
  if (!data?.usage) return null;
  const changed = plan !== data.usage.plan || addOns.join() !== [...data.usage.addOns].sort().join();

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
        <AddOnQuantities codes={data.addOns} value={addOns} onChange={setAddOns} />
        <button type="button" className="esc-button esc-button--primary" disabled={!changed} onClick={save}>
          Guardar plan
        </button>
      </div>
      {saveError && <Notice kind="error" title={saveError.detail} />}
      <UsageList usage={data.usage} />
    </Card>
  );
}
