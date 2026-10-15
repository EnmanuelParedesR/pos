import { useState } from "react";
import { useApi, type Shift } from "../api";
import { usePanel } from "../App";
import { cashierLabel, dateTime, money } from "../format";
import { Empty, LoadError, RangePicker, StoreSelect, lastDays } from "../ui";

/** Cuadres: closed shifts (Z) with expected, counted and the difference. */
export function ShiftsPage() {
  const { stores, storeId, setStoreId } = usePanel();
  const [range, setRange] = useState(lastDays(7));
  const [onlyDifferences, setOnlyDifferences] = useState(false);
  const { data, error, loading } = useApi<Shift[]>(
    `/api/panel/shifts?from=${range.from}&to=${range.to}&onlyDifferences=${onlyDifferences}${storeId ? `&storeId=${storeId}` : ""}`,
  );

  return (
    <>
      <div className="page__head">
        <h1 className="esc-title">Cuadres</h1>
        <div className="esc-row">
          <RangePicker value={range} onChange={setRange} />
          <StoreSelect stores={stores} value={storeId} onChange={setStoreId} />
          <button type="button" className="esc-chip" aria-pressed={onlyDifferences} onClick={() => setOnlyDifferences(!onlyDifferences)}>
            Solo con diferencias
          </button>
        </div>
      </div>
      <LoadError error={error} />

      <section className="esc-card esc-table-wrap">
        <table className="esc-table">
          <thead>
            <tr>
              <th>Cierre</th>
              <th className="esc-num">Diferencia</th>
              {stores.length > 1 && <th>Tienda</th>}
              <th>Z</th>
              <th>Cajero</th>
              <th className="esc-num">Esperado</th>
              <th className="esc-num">Contado</th>
            </tr>
          </thead>
          <tbody>
            {data?.map((s) => (
              <tr key={s.shiftId}>
                <td>{dateTime(s.closedUtc)}</td>
                <td className="esc-num">
                  <span className={`esc-pill esc-pill--${s.difference === 0 ? "success" : "warning"}`}>
                    {s.difference === 0 ? "Cuadró" : `${s.difference < 0 ? "Faltaron" : "Sobraron"} ${money(Math.abs(s.difference))}`}
                  </span>
                </td>
                {stores.length > 1 && <td>{s.storeName}</td>}
                <td>#{s.zNumber}</td>
                <td>{cashierLabel(s.cashierId)}</td>
                <td className="esc-num">{money(s.expectedCash)}</td>
                <td className="esc-num">{money(s.countedCash)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        {loading && !data && <Empty>Cargando…</Empty>}
        {data?.length === 0 && <Empty>{onlyDifferences ? "Ningún turno con diferencias en estas fechas." : "Ningún turno cerrado en estas fechas."}</Empty>}
      </section>
    </>
  );
}
