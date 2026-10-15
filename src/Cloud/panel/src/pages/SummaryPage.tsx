import { useState } from "react";
import { useApi, type Issue, type Summary } from "../api";
import { usePanel } from "../App";
import { ago, dateTime, isStale, money, paymentMethodNames, storeDay } from "../format";
import { Bars, Empty, Kpi, LoadError, Notice, StoreSelect } from "../ui";

export function SummaryPage() {
  const { stores, storeId, setStoreId } = usePanel();
  const [date, setDate] = useState(storeDay());
  const { data, error, loading } = useApi<Summary>(`/api/panel/summary?date=${date}${storeId ? `&storeId=${storeId}` : ""}`);

  return (
    <>
      <div className="page__head">
        <h1 className="esc-title">Resumen</h1>
        <div className="esc-row">
          <label className="esc-field">
            <span>Día</span>
            <input className="esc-input" type="date" value={date} max={storeDay()} onChange={(e) => e.target.value && setDate(e.target.value)} />
          </label>
          <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
        </div>
      </div>
      <LoadError error={error} />
      {loading && !data && <Empty>Cargando…</Empty>}
      {data && <SummaryView summary={data} />}
    </>
  );
}

function SummaryView({ summary: s }: { summary: Summary }) {
  const mixTotal = s.paymentMix.reduce((sum, p) => sum + p.amount, 0);
  const hours = s.byHour.length === 0 ? [] : range(s.byHour[0].hour, s.byHour[s.byHour.length - 1].hour);

  return (
    <>
      <LastUpdate utc={s.lastEventUtc} />

      <div className="kpis">
        <Kpi label="Vendido" value={money(s.salesTotal)} note={s.returnsTotal > 0 ? `Neto ${money(s.netTotal)} después de devoluciones` : undefined} />
        <Kpi label="Facturas" value={String(s.salesCount)} />
        <Kpi label="Ticket promedio" value={money(s.averageTicket)} />
        <Kpi label="Devoluciones" value={money(s.returnsTotal)} />
        <Kpi label="Anuladas" value={String(s.voidedCount)} />
      </div>

      {s.issues.length > 0 && (
        <section className="esc-col" aria-label="Para revisar">
          <h2 className="esc-label">Para revisar</h2>
          {s.issues.map((issue, i) => (
            <IssueCard key={i} issue={issue} />
          ))}
        </section>
      )}

      <div className="grid-2">
        <section className="esc-card esc-card--padded">
          <h2 className="esc-label">Ventas por hora</h2>
          {hours.length === 0 ? (
            <Empty>Sin ventas este día.</Empty>
          ) : (
            <Bars
              label="Ventas por hora"
              items={hours.map((h) => {
                const row = s.byHour.find((x) => x.hour === h);
                const total = row?.total ?? 0;
                return { key: String(h), label: hourLabel(h), value: total, title: `${hourLabel(h)}: ${money(total)} · ${row?.count ?? 0} facturas` };
              })}
            />
          )}
        </section>

        <section className="esc-card esc-card--padded">
          <h2 className="esc-label">Medios de pago</h2>
          {s.paymentMix.length === 0 ? (
            <Empty>Sin pagos este día.</Empty>
          ) : (
            <ul className="mix">
              {s.paymentMix.map((p) => (
                <li key={p.method}>
                  <div className="esc-row">
                    <span>{paymentMethodNames[p.method] ?? p.method}</span>
                    <span className="esc-muted">
                      {p.count} {p.count === 1 ? "pago" : "pagos"}
                    </span>
                    <span className="esc-spacer" />
                    <strong>{money(p.amount)}</strong>
                  </div>
                  <div className="mix__track">
                    <div style={{ width: `${mixTotal === 0 ? 0 : (p.amount / mixTotal) * 100}%` }} />
                  </div>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </>
  );
}

function IssueCard({ issue }: { issue: Issue }) {
  if (issue.kind === "ShiftDifference" && issue.amount !== null) {
    const title = `${issue.amount < 0 ? "Faltaron" : "Sobraron"} ${money(Math.abs(issue.amount))}`;
    return (
      <Notice kind="warning" title={title}>
        {issue.storeName}
        {issue.occurredUtc && ` · cierre del ${dateTime(issue.occurredUtc)}`}
      </Notice>
    );
  }
  return (
    <Notice kind="warning" title={issue.message}>
      {issue.storeName}
    </Notice>
  );
}

/** "Última actualización hace 5 min": amber after 24 h without data. */
export function LastUpdate({ utc }: { utc: string | null }) {
  const stale = isStale(utc);
  return (
    <p className={stale ? "esc-pill esc-pill--warning" : "esc-pill esc-pill--neutral"}>
      {utc ? `Última actualización ${ago(utc)}` : "Todavía no han llegado datos de la tienda"}
    </p>
  );
}

const range = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => from + i);

const hourLabel = (h: number) => `${h % 12 === 0 ? 12 : h % 12}${h < 12 ? "a" : "p"}`;
