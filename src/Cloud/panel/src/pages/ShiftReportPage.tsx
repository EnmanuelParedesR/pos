import { Link, useParams } from "react-router";
import { useApi, type ShiftReport } from "../api";
import { cashierLabel, dateTime, money, paymentMethodNames } from "../format";
import { LoadError, Logo, Skeleton } from "../ui";

/** The store's cash movement types (CashMovementRecordedV1), in the owner's words. */
const movementNames: Record<string, string> = {
  SalePayment: "Cobros de ventas",
  SalePaymentVoid: "Ventas anuladas",
  Refund: "Devoluciones",
  CashIn: "Entradas de efectivo",
  Withdrawal: "Salidas de efectivo",
  Expense: "Gastos",
  CustomerPayment: "Abonos de fiado",
  CustomerPaymentVoid: "Abonos anulados",
  Rounding: "Redondeo",
};

/** TK-200 in the panel: one closed shift (Z) on one A4 page; "Imprimir" prints it or saves it as PDF from the browser. */
export function ShiftReportPage() {
  const { shiftId = "" } = useParams();
  const { data, error, loading } = useApi<ShiftReport>(`/api/panel/shifts/${encodeURIComponent(shiftId)}`);
  const s = data?.shift;

  return (
    <>
      <div className="esc-row esc-no-print" style={{ gap: 8 }}>
        <Link className="esc-button esc-button--ghost" to="/app/shifts">
          Volver a Cuadres
        </Link>
        <span className="esc-spacer" />
        {data && (
          <button type="button" className="esc-button esc-button--primary" onClick={() => window.print()}>
            Imprimir o guardar PDF
          </button>
        )}
      </div>
      <LoadError error={error} />
      {loading && !data && <Skeleton height={320} />}
      {data && s && (
        <article className="esc-card esc-card--padded z-sheet" aria-label={`Cierre Z #${s.zNumber}`}>
          <header className="esc-row" style={{ gap: 12, alignItems: "flex-start" }}>
            <div className="esc-col" style={{ gap: 2 }}>
              <h1 style={{ margin: 0 }}>Cierre de turno Z #{s.zNumber}</h1>
              <span>{s.storeName}</span>
              <span className="esc-muted">
                {s.registerName ?? "Caja"} · {cashierLabel(s.cashierId, s.cashierName)}
              </span>
            </div>
            <span className="esc-spacer" />
            <Logo size={28} />
          </header>

          <dl className="z-sheet__grid">
            <dt>Abrió</dt>
            <dd>{data.openedUtc ? dateTime(data.openedUtc) : "—"}</dd>
            <dt>Cerró</dt>
            <dd>{dateTime(s.closedUtc)}</dd>
            <dt>Fondo inicial</dt>
            <dd>{data.openingFloat === null ? "—" : money(data.openingFloat)}</dd>
            <dt>Ventas</dt>
            <dd>
              {data.salesCount} · {money(data.salesTotal)}
            </dd>
            <dt>Anuladas</dt>
            <dd>{data.voidedCount}</dd>
          </dl>

          <h2 className="esc-card__title">Ventas por forma de pago</h2>
          <table className="esc-table">
            <tbody>
              {data.payments.map((p) => (
                <tr key={p.method}>
                  <td>{paymentMethodNames[p.method] ?? p.method}</td>
                  <td className="esc-num">{p.count}</td>
                  <td className="esc-num">{money(p.amount)}</td>
                </tr>
              ))}
              {data.payments.length === 0 && (
                <tr>
                  <td colSpan={3}>Sin ventas</td>
                </tr>
              )}
            </tbody>
          </table>

          {data.cashMovements.length > 0 && (
            <>
              <h2 className="esc-card__title">Movimientos de caja</h2>
              <table className="esc-table">
                <tbody>
                  {data.cashMovements.map((m) => (
                    <tr key={`${m.type}-${m.method}`}>
                      <td>
                        {movementNames[m.type] ?? m.type} · {paymentMethodNames[m.method] ?? m.method}
                      </td>
                      <td className="esc-num">{m.count}</td>
                      <td className="esc-num">{money(m.amount)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}

          <h2 className="esc-card__title">Efectivo</h2>
          <dl className="z-sheet__grid">
            <dt>Esperado</dt>
            <dd>{money(s.expectedCash)}</dd>
            <dt>Contado</dt>
            <dd>{money(s.countedCash)}</dd>
            <dt>Diferencia</dt>
            <dd style={{ fontWeight: 700 }}>{s.difference === 0 ? "Cuadró" : `${s.difference < 0 ? "Faltaron" : "Sobraron"} ${money(Math.abs(s.difference))}`}</dd>
          </dl>
          <p className="esc-muted" style={{ fontSize: 12 }}>
            Copia del panel de Toki con los datos que la tienda envió a la nube. El Z original se imprime en la caja.
          </p>
        </article>
      )}
    </>
  );
}
