import { useState } from "react";
import { Link } from "react-router";
import { useApi, type DaySales, type Issue, type ProductSales, type Receivables, type Summary } from "../api";
import { usePanel } from "../App";
import { addDays, ago, amount, customerLabel, dayName, isStale, money, paymentMethodNames, quantity, storeDay, storeHour, time, weekday } from "../format";
import { BarChart, BarList, Card, Delta, EmptyState, Icon, Legend, LoadError, PageHead, Segmented, Skeleton, Sparkline, Stat, StoreSelect } from "../ui";

type Day = "today" | "yesterday" | "other";

/** The owner's story of the day: what needs attention, how much and whether it is good, then the detail. */
export function SummaryPage() {
  const { stores, storeId, setStoreId } = usePanel();
  const [choice, setChoice] = useState<Day>("today");
  const [other, setOther] = useState(addDays(storeDay(), -2));
  const date = choice === "today" ? storeDay() : choice === "yesterday" ? addDays(storeDay(), -1) : other;
  const store = storeId ? `&storeId=${storeId}` : "";
  const summary = useApi<Summary>(`/api/panel/summary?date=${date}${store}`);
  // Same weekday last week, for the comparison (hour by hour, so "today so far" compares with "that day so far").
  const lastWeek = useApi<Summary>(`/api/panel/summary?date=${addDays(date, -7)}${store}`);
  const days = useApi<DaySales[]>(`/api/panel/sales/by-day?from=${addDays(date, -13)}&to=${date}${store}`);
  const top = useApi<ProductSales[]>(`/api/panel/sales/top-products?from=${date}&to=${date}&limit=6${store}`);
  const receivables = useApi<Receivables>(`/api/panel/receivables?limit=5${store}`);

  return (
    <>
      <PageHead title="Resumen" subtitle={<LastUpdate utc={summary.data?.lastEventUtc ?? null} loading={!summary.data} />}>
        <Segmented<Day> label="Día" options={[["today", "Hoy"], ["yesterday", "Ayer"], ["other", "Otro día"]]} value={choice} onChange={setChoice} />
        {choice === "other" && (
          <input className="esc-input" style={{ width: "auto" }} type="date" aria-label="Día" value={other} max={storeDay()} onChange={(e) => e.target.value && setOther(e.target.value)} />
        )}
        <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
      </PageHead>
      <LoadError error={summary.error} />
      {summary.loading && !summary.data && <SummarySkeleton />}
      {summary.data && (
        <SummaryView summary={summary.data} lastWeek={lastWeek.data} days={days.data ?? []} top={top.data ?? []} receivables={receivables.data} isToday={date === storeDay()} />
      )}
    </>
  );
}

function SummaryView({ summary: s, lastWeek, days, top, receivables, isToday }: { summary: Summary; lastWeek?: Summary; days: DaySales[]; top: ProductSales[]; receivables?: Receivables; isToday: boolean }) {
  // Today: last week's same day up to this hour; another day: the whole day.
  const hour = storeHour();
  const reference = lastWeek ? (isToday ? lastWeek.byHour.filter((h) => h.hour <= hour).reduce((t, h) => t + h.total, 0) : lastWeek.salesTotal) : 0;
  const lastWeekday = weekday(addDays(s.date, -7));
  const total = (d: string) => days.find((x) => x.date === d)?.total ?? 0;
  const week = Array.from({ length: 7 }, (_, i) => addDays(s.date, i - 6));
  const mixTotal = s.paymentMix.reduce((sum, p) => sum + p.amount, 0);
  const topMax = Math.max(0, ...top.map((p) => p.total));
  const hours = s.byHour.length === 0 ? [] : range(s.byHour[0].hour, s.byHour[s.byHour.length - 1].hour);

  return (
    <>
      {s.issues.length > 0 ? (
        <section className="esc-card" aria-labelledby="review-title">
          <div className="esc-card__head" style={{ padding: "16px 20px 0", margin: 0 }}>
            <h2 id="review-title" className="esc-card__title">
              Para revisar
            </h2>
            <span className="esc-card__aside">
              {s.issues.length} {s.issues.length === 1 ? "asunto" : "asuntos"}
            </span>
          </div>
          <ul className="esc-alerts">
            {s.issues.map((issue, i) => (
              <IssueRow key={i} issue={issue} />
            ))}
          </ul>
        </section>
      ) : (
        <div className="esc-all-good" role="status">
          <Icon name="check" /> Todo en orden: las cajas cuadraron y las tiendas están enviando datos.
        </div>
      )}

      <section className="esc-card" aria-label="Ventas del día">
        <div className="esc-hero">
          <div>
            <div className="esc-hero__label">{isToday ? "Vendido hoy" : `Vendido el ${dayName(s.date)}`}</div>
            <div className="esc-hero__value">
              <small>RD$</small>
              {amount(s.salesTotal)}
            </div>
            <div className="esc-hero__compare">
              {reference > 0 ? (
                <>
                  <Delta current={s.salesTotal} previous={reference} />
                  <span>
                    vs. el {lastWeekday} pasado{isToday ? " a esta hora" : ""} ({money(reference)})
                  </span>
                </>
              ) : (
                lastWeek && <span>El {lastWeekday} pasado no hubo ventas{isToday ? " a esta hora" : ""}.</span>
              )}
            </div>
          </div>
          <div className="esc-hero__spark">
            <Sparkline values={week.map(total)} />
          </div>
        </div>
        <div className="esc-stats" style={{ borderTop: "1px solid var(--line)" }}>
          <Stat icon="receipt" label="Facturas" value={String(s.salesCount)} note={s.voidedCount > 0 ? `${s.voidedCount} anuladas` : "Ninguna anulada"} />
          <Stat icon="cart" label="Ticket promedio" value={money(s.averageTicket)} />
          <Stat icon="cash" label="Devoluciones" value={money(s.returnsTotal)} note={s.returnsTotal > 0 ? `Neto ${money(s.netTotal)}` : "Ninguna"} />
        </div>
      </section>

      {s.ecfThisMonth > 0 && (
        <p className="ecf-month">
          <Icon name="receipt" size={18} /> Este mes emitió {s.ecfThisMonth.toLocaleString("en-US")} e-CF sin costo adicional.
        </p>
      )}

      <div className="esc-grid">
        <Card title="Ventas de los últimos 7 días" aside={<Legend current="Estos días" ghost="Semana anterior" />} className="esc-span-8">
          <BarChart
            label="Ventas de los últimos 7 días"
            empty="Cuando las tiendas vendan, aquí verá cada día comparado con la semana anterior."
            points={week.map((d) => {
              const row = days.find((x) => x.date === d);
              return {
                key: d,
                label: d === storeDay() ? "Hoy" : capitalize(dayName(d).split(",")[0]) + " " + Number(d.slice(8)),
                title: capitalize(dayName(d)),
                value: row?.total ?? 0,
                ghost: total(addDays(d, -7)),
                detail: row ? `${row.count} ${row.count === 1 ? "factura" : "facturas"}` : "Sin ventas",
                current: d === s.date,
              };
            })}
          />
        </Card>

        <Card title="Cómo le pagaron" className="esc-span-4">
          {s.paymentMix.length === 0 ? (
            <EmptyState icon="cash" title="Sin cobros este día" />
          ) : (
            <>
              <div className="esc-stack" aria-hidden="true">
                {s.paymentMix.map((p, i) => (
                  <div key={p.method} style={{ flex: p.amount, background: `var(--data-${(i % 4) + 1})` }} />
                ))}
              </div>
              <ul className="esc-barlist">
                {s.paymentMix.map((p, i) => (
                  <li key={p.method} className="esc-barlist__row">
                    <span className="esc-swatch" style={{ background: `var(--data-${(i % 4) + 1})` }} />
                    <span className="esc-barlist__name">{paymentMethodNames[p.method] ?? p.method}</span>
                    <span className="esc-barlist__share">{mixTotal === 0 ? 0 : Math.round((p.amount / mixTotal) * 100)} %</span>
                    <span className="esc-barlist__value">{money(p.amount)}</span>
                  </li>
                ))}
              </ul>
            </>
          )}
        </Card>

        {receivables && receivables.total > 0 && (
          <Card title="Le deben (cuentas por cobrar)" aside={`${money(receivables.total)} · ${receivables.customerCount} ${receivables.customerCount === 1 ? "cliente" : "clientes"}`} className="esc-span-12">
            <BarList
              items={receivables.topDebtors.map((d) => ({
                key: d.customerId,
                name: customerLabel(d.customerId, d.customerName),
                value: d.debt,
                share: receivables.topDebtors[0].debt === 0 ? 0 : d.debt / receivables.topDebtors[0].debt,
              }))}
            />
          </Card>
        )}

        <Card title="Lo más vendido" aside="Con ITBIS" className="esc-span-6">
          {top.length === 0 ? (
            <EmptyState icon="package" title="Sin ventas este día" />
          ) : (
            <BarList items={top.map((p) => ({ key: p.productId, name: p.description, detail: quantity(p.quantity, p.unit), value: p.total, share: topMax === 0 ? 0 : p.total / topMax }))} />
          )}
        </Card>

        <Card title="Ventas por hora" className="esc-span-6">
          <BarChart
            label="Ventas por hora"
            empty="Sin ventas este día."
            points={hours.map((h) => {
              const row = s.byHour.find((x) => x.hour === h);
              return { key: String(h), label: hourLabel(h), title: `De ${hourLabel(h)} a ${hourLabel(h + 1)}`, value: row?.total ?? 0, detail: `${row?.count ?? 0} facturas` };
            })}
          />
        </Card>
      </div>
    </>
  );
}

/** One thing to review, in words: amount, store, time and what to do. */
function IssueRow({ issue }: { issue: Issue }) {
  if (issue.kind === "ShiftDifference" && issue.amount !== null) {
    const short = issue.amount < 0;
    return (
      <li className={`esc-alert esc-alert--${short ? "error" : "warning"}`}>
        <span className="esc-alert__icon">
          <Icon name="cash" size={18} />
        </span>
        <div className="esc-alert__body">
          <div className="esc-alert__title">
            {short ? "Faltaron" : "Sobraron"} <b>{money(Math.abs(issue.amount))}</b> en {issue.registerName ?? issue.storeName}
          </div>
          <div className="esc-alert__meta">
            {[issue.cashierName, issue.registerName ? issue.storeName : null, issue.occurredUtc ? `cierre de ${dayLabel(issue.occurredUtc)}, ${time(issue.occurredUtc)}` : issue.message]
              .filter(Boolean)
              .join(" · ")}
          </div>
        </div>
        <Link className="esc-button esc-button--small" to="/app/shifts">
          Ver cuadres <Icon name="chevron-right" size={16} />
        </Link>
      </li>
    );
  }
  return (
    <li className="esc-alert esc-alert--warning">
      <span className="esc-alert__icon">
        <Icon name="cloud" size={18} />
      </span>
      <div className="esc-alert__body">
        <div className="esc-alert__title">{issue.message}</div>
        <div className="esc-alert__meta">{issue.storeName}</div>
      </div>
      <Link className="esc-button esc-button--small" to="/app/stores">
        Ver tienda <Icon name="chevron-right" size={16} />
      </Link>
    </li>
  );
}

function SummarySkeleton() {
  return (
    <>
      <div className="esc-card esc-hero">
        <div className="esc-col" style={{ gap: 12 }}>
          <Skeleton width={120} />
          <Skeleton width={260} height={44} />
          <Skeleton width={220} />
        </div>
      </div>
      <div className="esc-grid">
        <div className="esc-card esc-card--padded esc-span-8">
          <Skeleton height={220} />
        </div>
        <div className="esc-card esc-card--padded esc-span-4">
          <Skeleton height={220} />
        </div>
      </div>
    </>
  );
}

/** "Actualizado hace 5 min": amber after 24 h without data (confidence: how fresh are these numbers?). */
export function LastUpdate({ utc, loading = false }: { utc: string | null; loading?: boolean }) {
  if (loading) return <>Cargando…</>;
  return (
    <span className={`esc-pill esc-pill--${isStale(utc) ? "warning" : "success"}`}>
      {utc ? `Actualizado ${ago(utc)}` : "Todavía no han llegado datos de la tienda"}
    </span>
  );
}

const range = (from: number, to: number) => Array.from({ length: to - from + 1 }, (_, i) => from + i);
const hourLabel = (h: number) => `${h % 12 === 0 ? 12 : h % 12}${h < 12 || h === 24 ? "a" : "p"}`;
const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);
const dayLabel = (utc: string) => {
  const d = storeDay(new Date(utc));
  return d === storeDay() ? "hoy" : d === addDays(storeDay(), -1) ? "ayer" : dayName(d);
};
