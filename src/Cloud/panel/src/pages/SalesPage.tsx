import { useState } from "react";
import { useApi, type CashierSales, type DaySales, type ProductSales } from "../api";
import { usePanel } from "../App";
import { cashierLabel, dayName, money, quantity } from "../format";
import { Bars, Empty, LoadError, RangePicker, StoreSelect, lastDays } from "../ui";

export function SalesPage() {
  const { stores, storeId, setStoreId } = usePanel();
  const [range, setRange] = useState(lastDays(7));
  const query = `from=${range.from}&to=${range.to}${storeId ? `&storeId=${storeId}` : ""}`;
  const days = useApi<DaySales[]>(`/api/panel/sales/by-day?${query}`);
  const products = useApi<ProductSales[]>(`/api/panel/sales/top-products?${query}&limit=10`);
  const cashiers = useApi<CashierSales[]>(`/api/panel/sales/by-cashier?${query}`);

  return (
    <>
      <div className="page__head">
        <h1 className="esc-title">Ventas</h1>
        <div className="esc-row">
          <RangePicker value={range} onChange={setRange} />
          <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
        </div>
      </div>
      <LoadError error={days.error ?? products.error ?? cashiers.error} />

      <section className="esc-card esc-card--padded">
        <h2 className="esc-label">Ventas por día</h2>
        {days.data && (
          <Bars
            label="Ventas por día"
            items={days.data.map((d) => ({ key: d.date, label: d.date.slice(8), value: d.total, title: `${dayName(d.date)}: ${money(d.total)} · ${d.count} facturas` }))}
          />
        )}
      </section>

      <section className="esc-card esc-table-wrap">
        <table className="esc-table">
          <thead>
            <tr>
              <th>Día</th>
              <th className="esc-num">Facturas</th>
              <th className="esc-num">Vendido</th>
              <th className="esc-num">Devoluciones</th>
              <th className="esc-num">Neto</th>
              <th className="esc-num">Anuladas</th>
            </tr>
          </thead>
          <tbody>
            {[...(days.data ?? [])].reverse().map((d) => (
              <tr key={d.date}>
                <td>{dayName(d.date)}</td>
                <td className="esc-num">{d.count}</td>
                <td className="esc-num">{money(d.total)}</td>
                <td className="esc-num">{money(d.returns)}</td>
                <td className="esc-num">{money(d.net)}</td>
                <td className="esc-num">{d.voidedCount}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <div className="grid-2">
        <section className="esc-card esc-table-wrap">
          <table className="esc-table">
            <thead>
              <tr>
                <th>Más vendidos</th>
                <th className="esc-num">Cantidad</th>
                <th className="esc-num">Vendido</th>
                <th className="esc-num">Ganancia</th>
              </tr>
            </thead>
            <tbody>
              {products.data?.map((p) => (
                <tr key={p.productId}>
                  <td>{p.description}</td>
                  <td className="esc-num">{quantity(p.quantity)}</td>
                  <td className="esc-num">{money(p.total)}</td>
                  <td className="esc-num">{money(p.profit)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {products.data?.length === 0 && <Empty>Sin ventas en estas fechas.</Empty>}
        </section>

        <section className="esc-card esc-table-wrap">
          <table className="esc-table">
            <thead>
              <tr>
                <th>Por cajero</th>
                <th className="esc-num">Facturas</th>
                <th className="esc-num">Vendido</th>
                <th className="esc-num">Anuladas</th>
              </tr>
            </thead>
            <tbody>
              {cashiers.data?.map((c) => (
                <tr key={c.cashierId}>
                  <td>{cashierLabel(c.cashierId)}</td>
                  <td className="esc-num">{c.count}</td>
                  <td className="esc-num">{money(c.total)}</td>
                  <td className="esc-num">{c.voidedCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {cashiers.data?.length === 0 && <Empty>Sin ventas en estas fechas.</Empty>}
        </section>
      </div>
    </>
  );
}
