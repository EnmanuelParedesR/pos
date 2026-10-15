import { useState } from "react";
import { useApi, type CashierSales, type DaySales, type ProductSales } from "../api";
import { usePanel } from "../App";
import { addDays, cashierLabel, dayName, money, quantity, rangeLabel, storeDay } from "../format";
import { BarChart, BarList, Card, Delta, EmptyState, Legend, LoadError, PageHead, RangePicker, SkeletonRows, StoreSelect, lastDays, previousRange } from "../ui";

export function SalesPage() {
  const { stores, storeId, setStoreId } = usePanel();
  const [range, setRange] = useState(lastDays(7));
  const before = previousRange(range);
  const store = storeId ? `&storeId=${storeId}` : "";
  const query = `from=${range.from}&to=${range.to}${store}`;
  // The previous period of the same length, for the comparison and the chart's ghost bars.
  const days = useApi<DaySales[]>(`/api/panel/sales/by-day?from=${before.from}&to=${range.to}${store}`);
  const products = useApi<ProductSales[]>(`/api/panel/sales/top-products?${query}&limit=10`);
  const cashiers = useApi<CashierSales[]>(`/api/panel/sales/by-cashier?${query}`);

  const inRange = (d: DaySales) => d.date >= range.from && d.date <= range.to;
  const current = (days.data ?? []).filter(inRange);
  const previous = (days.data ?? []).filter((d) => !inRange(d));
  const sum = (list: DaySales[], f: (d: DaySales) => number) => list.reduce((t, d) => t + f(d), 0);
  const sold = sum(current, (d) => d.total);
  const count = sum(current, (d) => d.count);
  const length = Math.round((Date.parse(range.to) - Date.parse(range.from)) / 86_400_000) + 1;
  const dates = Array.from({ length }, (_, i) => addDays(range.from, i));
  const byDate = new Map((days.data ?? []).map((d) => [d.date, d]));
  const productMax = Math.max(0, ...(products.data ?? []).map((p) => p.total));

  return (
    <>
      <PageHead title="Ventas" subtitle={rangeLabel(range.from, range.to)}>
        <RangePicker value={range} onChange={setRange} />
        <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
      </PageHead>
      <LoadError error={days.error ?? products.error ?? cashiers.error} />

      <section className="esc-card" aria-label="Totales del periodo">
        <div className="esc-stats">
          <div className="esc-stat">
            <span className="esc-stat__label">Vendido</span>
            <span className="esc-stat__value">{money(sold)}</span>
            <span className="esc-stat__note esc-row" style={{ gap: 6 }}>
              {sum(previous, (d) => d.total) > 0 ? (
                <>
                  <Delta current={sold} previous={sum(previous, (d) => d.total)} /> vs. {money(sum(previous, (d) => d.total))} en los {length} días anteriores
                </>
              ) : (
                `Sin ventas en los ${length} días anteriores`
              )}
            </span>
          </div>
          <div className="esc-stat">
            <span className="esc-stat__label">Facturas</span>
            <span className="esc-stat__value">{count}</span>
            <span className="esc-stat__note">{sum(previous, (d) => d.count)} en el periodo anterior</span>
          </div>
          <div className="esc-stat">
            <span className="esc-stat__label">Ticket promedio</span>
            <span className="esc-stat__value">{money(count === 0 ? 0 : sold / count)}</span>
            <span className="esc-stat__note">Devoluciones: {money(sum(current, (d) => d.returns))}</span>
          </div>
        </div>
      </section>

      <Card title="Ventas por día" aside={<Legend current="Este periodo" ghost="Periodo anterior" />}>
        {days.data ? (
          <BarChart
            label="Ventas por día"
            ghostLabel="Periodo anterior"
            empty="Sin ventas en estas fechas."
            points={dates.map((d, i) => {
              const row = byDate.get(d);
              return {
                key: d,
                label: length <= 7 ? dayName(d).split(",")[0] + " " + Number(d.slice(8)) : String(Number(d.slice(8))),
                title: dayName(d),
                value: row?.total ?? 0,
                ghost: byDate.get(addDays(before.from, i))?.total ?? 0,
                detail: row ? `${row.count} facturas` : "Sin ventas",
                current: d === storeDay(),
              };
            })}
          />
        ) : (
          <div style={{ height: 228 }} />
        )}
      </Card>

      <div className="esc-grid">
        <Card title="Lo más vendido" aside="Con ITBIS · ganancia sin ITBIS" className="esc-span-6">
          {products.data?.length === 0 ? (
            <EmptyState icon="package" title="Sin ventas en estas fechas" />
          ) : (
            <BarList
              items={(products.data ?? []).map((p) => ({
                key: p.productId,
                name: p.description,
                detail: `${quantity(p.quantity)} · gana ${money(p.profit)}`,
                value: p.total,
                share: productMax === 0 ? 0 : p.total / productMax,
              }))}
            />
          )}
        </Card>

        <section className="esc-card esc-span-6" aria-label="Por cajero">
          <div className="esc-card__head" style={{ padding: "20px 20px 0", margin: 0 }}>
            <h2 className="esc-card__title">Por cajero</h2>
          </div>
          <div className="esc-table-wrap">
            <table className="esc-table">
              <thead>
                <tr>
                  <th>Cajero</th>
                  <th className="esc-num">Facturas</th>
                  <th className="esc-num">Vendido</th>
                  <th className="esc-num">Anuladas</th>
                </tr>
              </thead>
              <tbody>
                {cashiers.loading && !cashiers.data && <SkeletonRows columns={4} rows={3} />}
                {cashiers.data?.map((c) => (
                  <tr key={c.cashierId}>
                    <td>{cashierLabel(c.cashierId, c.cashierName)}</td>
                    <td className="esc-num">{c.count}</td>
                    <td className="esc-num">{money(c.total)}</td>
                    <td className="esc-num">{c.voidedCount}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {cashiers.data?.length === 0 && <EmptyState title="Sin ventas en estas fechas" />}
          </div>
        </section>
      </div>

      <section className="esc-card" aria-label="Día por día">
        <div className="esc-card__head" style={{ padding: "20px 20px 0", margin: 0 }}>
          <h2 className="esc-card__title">Día por día</h2>
        </div>
        <div className="esc-table-wrap esc-table-wrap--tall">
          <table className="esc-table esc-table--wide">
            <thead>
              <tr>
                <th>Día</th>
                <th className="esc-num">Facturas</th>
                <th className="esc-num">Vendido</th>
                <th className="esc-num esc-hide-sm">Devoluciones</th>
                <th className="esc-num esc-hide-sm">Neto</th>
                <th className="esc-num esc-hide-sm">Anuladas</th>
              </tr>
            </thead>
            <tbody>
              {days.loading && !days.data && <SkeletonRows columns={6} />}
              {[...current].reverse().map((d) => (
                <tr key={d.date} style={d.count === 0 ? { color: "var(--text-3)" } : undefined}>
                  <td>{dayName(d.date)}</td>
                  <td className="esc-num">{d.count}</td>
                  <td className="esc-num" style={{ fontWeight: 600 }}>
                    {money(d.total)}
                  </td>
                  <td className="esc-num esc-hide-sm">{money(d.returns)}</td>
                  <td className="esc-num esc-hide-sm">{money(d.net)}</td>
                  <td className="esc-num esc-hide-sm">{d.voidedCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {days.data && current.length === 0 && <EmptyState icon="chart" title="Sin ventas en estas fechas" />}
        </div>
      </section>
    </>
  );
}
