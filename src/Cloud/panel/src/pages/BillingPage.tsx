import { useEffect, useState } from "react";
import { Link } from "react-router";
import { api, ApiError, priceLabel, useApi, type Billing, type BillingInterval, type Checkout, type PlatformBilling, type SubscriptionStatus } from "../api";
import { dateTime, dayName } from "../format";
import { Card, LoadError, Notice, PageHead, Segmented, Skeleton } from "../ui";
import { AddOnQuantities, addOnList, planNames } from "./UsageCard";
import { ReferralCard } from "./ReferralCards";
import { forgetReferral, keptReferral } from "../referral";

/** Paddle.js v2, from Paddle's CDN only (Paddle asks to load it from cdn.paddle.com; the /v2/ path is the version it pins). Only this page loads it. */
export const paddleScript = "https://cdn.paddle.com/paddle/v2/paddle.js";

interface PaddleEvent { name?: string }
interface PaddleJs {
  Environment: { set(environment: string): void };
  Initialize(options: { token: string; eventCallback?: (event: PaddleEvent) => void }): void;
  Checkout: { open(options: unknown): void };
}

declare global {
  interface Window { Paddle?: PaddleJs }
}

let loading: Promise<PaddleJs> | null = null;
let initialized = false;
let onPaddleEvent: (event: PaddleEvent) => void = () => {};

function loadPaddle(): Promise<PaddleJs> {
  if (window.Paddle) return Promise.resolve(window.Paddle);
  loading ??= new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = paddleScript;
    script.async = true;
    script.onload = () => (window.Paddle ? resolve(window.Paddle) : reject(new Error("Paddle.js")));
    script.onerror = () => {
      loading = null;
      script.remove();
      reject(new ApiError(0, "Network", "No se pudo abrir el pago. Revise el internet e intente de nuevo."));
    };
    document.head.appendChild(script);
  });
  return loading;
}

export const statusNames: Record<SubscriptionStatus, string> = {
  None: "Sin suscripción",
  Trialing: "En prueba",
  Active: "Al día",
  PastDue: "Pago pendiente",
  Paused: "En pausa",
  Canceled: "Cancelada",
};

/** TK-313: the plans that can add another store ("Sucursal adicional"); on Esencial it would cost more than the plan. */
const multiStorePlans = ["MultiRegister", "MultiStore"];

const day = (utc: string | null) => (utc ? dayName(utc.slice(0, 10)) : "—");

/** "Plan y facturación" (TK-249, Owners): the plan, its status and the buttons to pay with Paddle (overlay checkout) or manage the payment (Paddle's portal). */
export function BillingPage() {
  const { data, error, reload } = useApi<Billing>("/api/billing");
  const [interval, setBillingInterval] = useState<BillingInterval>("Monthly");
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<ApiError | null>(null);
  const [done, setDone] = useState<string | null>(null);
  // TK-258: add-ons, one entry per unit. TK-305: the owner's picks hold only for the data they were made on (no fill-in effect racing a click).
  const [picked, setPicked] = useState<{ on: Billing; addOns: string[] } | null>(null);

  useEffect(() => {
    if (data?.enabled) void loadPaddle().catch(() => undefined); // ready before the click; a failure shows on the click
  }, [data?.enabled]);

  if (error) return <LoadError error={error} />;
  if (!data) return <Skeleton height={160} />;

  const subscribed = data.status !== "None" && data.status !== "Canceled" && data.canManage;
  const annual = data.plans.some((p) => p.annual);
  const yearly = (subscribed && data.interval === "Annual") || (!subscribed && interval === "Annual");
  const chosen = picked?.on === data ? picked.addOns : [...data.addOns].sort();
  const setChosen = (addOns: string[]) => setPicked({ on: data, addOns });
  const addOnsChanged = chosen.join() !== [...data.addOns].sort().join();

  async function buy(plan: string | null, addOns: string[]) {
    setBusy(true);
    setActionError(null);
    setDone(null);
    try {
      const referralCode = subscribed ? null : keptReferral(); // TK-255: the code from /precios?ref=, until the first checkout
      const r = await api<Checkout | { changed: boolean }>("/api/billing/checkout", "POST", { plan, addOns, interval, ...(referralCode ? { referralCode } : {}) });
      if ("changed" in r) {
        setDone("Listo. Paddle cobra o acredita la diferencia y su plan cambia en unos segundos.");
        window.setTimeout(reload, 4000);
        return;
      }
      const paddle = await loadPaddle();
      onPaddleEvent = (e) => {
        if (e.name === "checkout.completed") {
          forgetReferral(); // the cloud keeps it now
          setDone("¡Gracias! Recibimos su pago. Su plan se activa en unos segundos, también en la tienda.");
          window.setTimeout(reload, 4000);
        }
      };
      if (!initialized) {
        if (r.environment === "sandbox") paddle.Environment.set("sandbox");
        paddle.Initialize({ token: r.clientToken, eventCallback: (e) => onPaddleEvent(e) });
        initialized = true;
      }
      paddle.Checkout.open({
        items: r.items,
        customer: r.customerEmail ? { email: r.customerEmail } : undefined,
        customData: r.customData,
        settings: { displayMode: "overlay", locale: "es" },
      });
    } catch (err) {
      setActionError(err instanceof ApiError ? err : new ApiError(0, "Network", "No se pudo abrir el pago. Revise el internet e intente de nuevo."));
    } finally {
      setBusy(false);
    }
  }

  async function manage() {
    setBusy(true);
    setActionError(null);
    try {
      const { url } = await api<{ url: string }>("/api/billing/portal", "POST");
      window.location.assign(url);
    } catch (err) {
      setActionError(err as ApiError);
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHead title="Plan y facturación" subtitle="Su plan de Abastalia, cómo va el pago y su consumo del mes. Paddle cobra por Abastalia y le envía la factura." />

      {data.status === "PastDue" && (
        <Notice kind="warning" title="No pudimos cobrar su tarjeta.">
          Paddle lo intentará de nuevo. Actualice su tarjeta en «Administrar pago». Si no se paga, al vencer su licencia la tienda tiene 15 días de gracia y luego
          pasa a modo restringido: siempre puede vender, facturar y exportar sus datos.
        </Notice>
      )}
      {done && <Notice kind="success" title={done} />}
      {actionError && <Notice kind={actionError.offline ? "warning" : "error"} title={actionError.detail} />}

      <Card title="Su plan" aside={<Link to="/app/stores">Ver consumo del mes</Link>}>
        <ul className="platform-kv" aria-label="Su plan">
          <li>
            <span>Plan</span>
            <b>
              {planNames[data.plan] ?? data.plan}
              {data.addOns.length > 0 && ` + ${addOnList(data.addOns)}`}
            </b>
          </li>
          <li>
            <span>Estado</span>
            <span className={`esc-pill esc-pill--${data.status === "PastDue" ? "warning" : data.status === "Active" || data.status === "Trialing" ? "success" : "neutral"}`}>{statusNames[data.status]}</span>
          </li>
          {data.paidThroughUtc && (
            <li>
              <span>Pagado hasta</span>
              <span>{day(data.paidThroughUtc)}</span>
            </li>
          )}
          {data.nextBilledUtc && (
            <li>
              <span>Próximo cobro</span>
              <span>
                {day(data.nextBilledUtc)}
                {data.interval && ` · ${data.interval === "Annual" ? "anual" : "mensual"}`}
              </span>
            </li>
          )}
          {data.manualUntilUtc && (
            <li>
              <span>Acuerdo con Abastalia</span>
              <span>Hasta el {day(data.manualUntilUtc)}</span>
            </li>
          )}
        </ul>
        {data.canManage && (
          <div className="esc-row" style={{ marginTop: 12 }}>
            <button type="button" className="esc-button" disabled={busy} onClick={manage}>
              Administrar pago
            </button>
            <span className="esc-muted">Cambie la tarjeta, vea sus facturas o cancele en el portal de Paddle.</span>
          </div>
        )}
      </Card>

      {!data.enabled ? (
        <Notice kind="info" title="El pago en línea todavía no está disponible.">
          Para contratar o cambiar de plan, <Link to="/precios#contacto">escríbanos</Link> y se lo activamos.
        </Notice>
      ) : (
        <Card title="Elegir plan" aside={subscribed ? "El cambio se prorratea al momento." : "Pague con tarjeta; Paddle calcula los impuestos."}>
          {annual && !subscribed && (
            <Segmented
              label="Forma de pago"
              options={[
                ["Monthly", "Mensual"],
                ["Annual", "Anual"],
              ]}
              value={interval}
              onChange={setBillingInterval}
            />
          )}
          <ul className="platform-kv" aria-label="Planes">
            {data.plans
              // R12 #10: an annual subscriber sees the annual prices of the plans to change to (the same interval as the subscription).
              .filter((p) => (yearly ? p.annual : p.monthly) || subscribed)
              .sort((a, b) => Number((a.monthly ?? a.annual)?.amount ?? 0) - Number((b.monthly ?? b.annual)?.amount ?? 0)) // cheapest first
              .map((p) => {
                const current = subscribed && p.code === data.plan;
                const price = (yearly ? p.annual : p.monthly) ?? p.monthly ?? p.annual;
                return (
                  <li key={p.code}>
                    <span>
                      {planNames[p.code] ?? p.name}
                      {price && <span className="esc-muted"> · {priceLabel(price)}</span>}
                    </span>
                    <button type="button" className={current ? "esc-button" : "esc-button esc-button--primary"} disabled={busy || current}
                      onClick={() => buy(p.code, chosen)}>
                      {current ? "Su plan" : "Elegir plan"}
                    </button>
                  </li>
                );
              })}
          </ul>
          {data.addOnOptions.length > 0 && (
            <>
              <h3 style={{ marginTop: 16 }}>Adapte su plan</h3>
              <div className="esc-row" style={{ gap: 12, flexWrap: "wrap" }}>
                <AddOnQuantities value={chosen} onChange={setChosen}
                  // TK-313: another store only with Pro or more (one that already has it can still remove it)
                  codes={data.addOnOptions.map((a) => a.code).filter((c) => c !== "ExtraStore" || multiStorePlans.includes(data.plan) || chosen.includes(c))}
                  note={(code) => {
                    const option = data.addOnOptions.find((a) => a.code === code);
                    const price = option && (yearly ? option.annual : option.monthly);
                    return price ? `${priceLabel(price)} c/u` : null;
                  }} />
                {subscribed && (
                  <button type="button" className="esc-button esc-button--primary" disabled={busy || !addOnsChanged} onClick={() => buy(null, chosen)}>
                    Actualizar adicionales
                  </button>
                )}
              </div>
              {!subscribed && <p className="esc-muted">Elija las cantidades y luego su plan.</p>}
            </>
          )}
        </Card>
      )}

      <ReferralCard />
    </>
  );
}

/** Abastalia staff (TK-249): the business's Paddle subscription and the override for special deals (audited like every platform call). */
export function PlatformBillingCard({ tenantId }: { tenantId: string }) {
  const { data, error, reload } = useApi<PlatformBilling>(`/api/platform/tenants/${tenantId}/billing`);
  const [until, setUntil] = useState("");
  const [note, setNote] = useState("");
  const [saveError, setSaveError] = useState<ApiError | null>(null);
  const [blockReason, setBlockReason] = useState("");
  const [unblockedUnpaid, setUnblockedUnpaid] = useState(false);
  if (error) return <LoadError error={error} />;
  if (!data) return null;
  const s = data.subscription;
  // TK-287: without paid time the cloud issues no license, so an unblocked store keeps its blocked one until staff set an agreement.
  const paid = [s.paidThroughUtc, s.manualUntilUtc].some((d) => d && Date.parse(d) > Date.now());

  // TK-279/TK-287: the stores stop selling once they pull the next license (with their next upload; a blocked one asks every 5 minutes).
  async function block(blocked: boolean) {
    try {
      await api(`/api/platform/tenants/${tenantId}/block`, "PUT", { blocked, reason: blockReason });
      setUnblockedUnpaid(!blocked && !paid);
      setSaveError(null);
      setBlockReason("");
      reload();
    } catch (err) {
      setSaveError(err as ApiError);
    }
  }

  async function save(manualUntilUtc: string | null) {
    try {
      await api(`/api/platform/tenants/${tenantId}/billing`, "PUT", { manualUntilUtc, note: manualUntilUtc ? note : null });
      setSaveError(null);
      setUntil("");
      setNote("");
      reload();
    } catch (err) {
      setSaveError(err as ApiError);
    }
  }

  return (
    <Card title="Suscripción" aside={statusNames[s.status]}>
      <ul className="platform-kv" aria-label="Suscripción">
        <li>
          <span>Paddle</span>
          <span className="esc-muted" style={{ overflowWrap: "anywhere" }}>{[s.paddleCustomerId, s.paddleSubscriptionId].filter(Boolean).join(" · ") || "—"}</span>
        </li>
        <li>
          <span>Pagado hasta</span>
          <span>{day(s.paidThroughUtc)}</span>
        </li>
        <li>
          <span>Próximo cobro</span>
          <span>{day(s.nextBilledUtc)}</span>
        </li>
        {s.pastDueSinceUtc && (
          <li>
            <span>Pago pendiente desde</span>
            <span className="esc-pill esc-pill--warning">{day(s.pastDueSinceUtc)}</span>
          </li>
        )}
        <li>
          <span>Acuerdo manual</span>
          <span>{s.manualUntilUtc ? `Hasta el ${day(s.manualUntilUtc)} · ${s.manualNote ?? ""}` : "—"}</span>
        </li>
      </ul>
      <div className="esc-row" style={{ gap: 12, flexWrap: "wrap", marginTop: 12 }}>
        <label className="esc-field">
          <span>Pagado hasta (acuerdo)</span>
          <input className="esc-input" type="date" value={until} onChange={(e) => setUntil(e.target.value)} />
        </label>
        <label className="esc-field" style={{ minWidth: 240, flex: 1 }}>
          <span>Motivo</span>
          <input className="esc-input" value={note} maxLength={500} placeholder="Acuerdo, pago por transferencia…" onChange={(e) => setNote(e.target.value)} />
        </label>
        <button type="button" className="esc-button esc-button--primary" disabled={!until || !note.trim()} onClick={() => save(`${until}T23:59:59Z`)}>
          Guardar acuerdo
        </button>
        {s.manualUntilUtc && (
          <button type="button" className="esc-button" onClick={() => save(null)}>
            Quitar acuerdo
          </button>
        )}
      </div>
      <ul className="platform-kv" aria-label="Bloqueo" style={{ marginTop: 12 }}>
        <li>
          <span>Bloqueo</span>
          <span>
            {s.blockedUtc ? <span className="esc-pill esc-pill--error">Bloqueado desde el {day(s.blockedUtc)}</span> : "No"}
            {s.blockNote && <span className="esc-muted"> · {s.blockNote}</span>}
          </span>
        </li>
      </ul>
      {!paid && (s.blockedUtc || unblockedUnpaid) && (
        <Notice kind="warning" title={s.blockedUtc ? "Este negocio no tiene tiempo pagado." : "Desbloqueado, pero sus tiendas siguen bloqueadas."}>
          {s.blockedUtc
            ? "Desbloquearlo no basta: sus tiendas siguen bloqueadas hasta que ponga un acuerdo en «Pagado hasta (acuerdo)»."
            : "No tiene tiempo pagado: ponga un acuerdo en «Pagado hasta (acuerdo)» para que vuelvan a vender."}
        </Notice>
      )}
      <div className="esc-row" style={{ gap: 12, flexWrap: "wrap", marginTop: 12 }}>
        <label className="esc-field" style={{ minWidth: 240, flex: 1 }}>
          <span>Motivo del bloqueo o desbloqueo</span>
          <input className="esc-input" value={blockReason} maxLength={500} placeholder="Falta de pago, acuerdo…" onChange={(e) => setBlockReason(e.target.value)} />
        </label>
        <button type="button" className={s.blockedUtc ? "esc-button esc-button--primary" : "esc-button esc-button--danger"} disabled={!blockReason.trim()}
          onClick={() => block(!s.blockedUtc)}>
          {s.blockedUtc ? "Desbloquear negocio" : "Bloquear negocio"}
        </button>
      </div>
      <p className="esc-muted" style={{ marginTop: 4 }}>
        Bloqueado, sus tiendas no venden ni abren turnos (cierran el turno abierto, reportan y respaldan). Llega a las tiendas conectadas en minutos.
      </p>
      {saveError && <Notice kind="error" title={saveError.detail} />}
      {data.events.length > 0 && (
        <ul className="platform-kv" aria-label="Eventos de Paddle" style={{ marginTop: 12 }}>
          {data.events.map((e) => (
            <li key={e.id}>
              <span>{e.type}</span>
              <span className="esc-muted">{dateTime(e.occurredUtc)}</span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}
