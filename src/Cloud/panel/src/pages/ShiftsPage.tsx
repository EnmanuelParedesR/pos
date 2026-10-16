import { useState } from "react";
import { Link } from "react-router";
import { useApi, type Shift } from "../api";
import { usePanel } from "../App";
import { cashierLabel, dateTime, money } from "../format";
import { EmptyState, LoadError, PageHead, RangePicker, SkeletonRows, StoreSelect, lastDays } from "../ui";

/** Cuadres: closed shifts (Z) with expected, counted and the difference; each opens its printable Z (TK-200). */
export function ShiftsPage() {
  const { stores, storeId, setStoreId } = usePanel();
  const [range, setRange] = useState(lastDays(7));
  const [onlyDifferences, setOnlyDifferences] = useState(false);
  const { data, error, loading } = useApi<Shift[]>(
    `/api/panel/shifts?from=${range.from}&to=${range.to}&onlyDifferences=${onlyDifferences}${storeId ? `&storeId=${storeId}` : ""}`,
  );
  const missing = (data ?? []).filter((s) => s.difference < 0).reduce((t, s) => t + s.difference, 0);

  return (
    <>
      <PageHead title="Cuadres" subtitle="Cada cierre con lo que se esperaba en efectivo y lo que se contó.">
        <RangePicker value={range} onChange={setRange} />
        <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
      </PageHead>
      <LoadError error={error} />

      <div className="esc-row" style={{ gap: 12 }}>
        <button type="button" className="esc-chip" aria-pressed={onlyDifferences} onClick={() => setOnlyDifferences(!onlyDifferences)}>
          Solo con diferencias
        </button>
        {data && data.length > 0 && (
          <span className="esc-muted">
            {data.length} {data.length === 1 ? "cierre" : "cierres"} · {data.filter((s) => s.difference === 0).length} cuadraron
            {missing < 0 && ` · faltaron ${money(-missing)} en total`}
          </span>
        )}
      </div>

      <section className="esc-card esc-table-wrap esc-table-wrap--tall">
        <table className="esc-table esc-table--wide">
          <thead>
            <tr>
              <th>Cierre</th>
              <th>Cuadre</th>
              {stores.length > 1 && <th className="esc-hide-sm">Tienda</th>}
              <th className="esc-hide-sm">Cajero</th>
              <th className="esc-hide-sm">Caja</th>
              <th className="esc-num esc-hide-sm">Esperado</th>
              <th className="esc-num esc-hide-sm">Contado</th>
              <th className="esc-num esc-hide-sm">Z</th>
            </tr>
          </thead>
          <tbody>
            {loading && !data && <SkeletonRows columns={7} />}
            {data?.map((s) => (
              <tr key={s.shiftId}>
                <td>
                  {/* TK-200: the Z on one printable page. */}
                  <Link to={`/shifts/${s.shiftId}`} title="Ver e imprimir el Z">
                    {dateTime(s.closedUtc)}
                  </Link>
                  <span className="esc-table__sub esc-only-sm">
                    {[cashierLabel(s.cashierId, s.cashierName), s.registerName ?? s.storeName].join(" · ")}
                  </span>
                </td>
                <td>
                  <span className={`esc-pill esc-pill--${s.difference === 0 ? "success" : s.difference < 0 ? "error" : "warning"}`}>
                    {s.difference === 0 ? "Cuadró" : `${s.difference < 0 ? "Faltaron" : "Sobraron"} ${money(Math.abs(s.difference))}`}
                  </span>
                </td>
                {stores.length > 1 && <td className="esc-hide-sm">{s.storeName}</td>}
                <td className="esc-hide-sm">{cashierLabel(s.cashierId, s.cashierName)}</td>
                <td className="esc-hide-sm">{s.registerName ?? "—"}</td>
                <td className="esc-num esc-hide-sm">{money(s.expectedCash)}</td>
                <td className="esc-num esc-hide-sm">{money(s.countedCash)}</td>
                <td className="esc-num esc-muted esc-hide-sm">#{s.zNumber}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {data?.length === 0 && (
          <EmptyState icon="wallet" title={onlyDifferences ? "Ningún cierre con diferencias" : "Ningún turno cerrado en estas fechas"}>
            {onlyDifferences ? "Todas las cajas cuadraron en estas fechas." : "Los cierres aparecen aquí cuando una caja conectada cierra su turno."}
          </EmptyState>
        )}
      </section>
    </>
  );
}
