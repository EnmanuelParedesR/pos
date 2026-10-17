import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { Link, useLocation, useNavigate, useParams } from "react-router";
import {
  api,
  ApiError,
  roleNames,
  useApi,
  type AccompanimentStatus,
  type CertificationProgress,
  type DailyActivity,
  type FeDelegation,
  type FunnelStage,
  type MetricsOverview,
  type PlatformAccompaniment,
  type PlatformNode,
  type PlatformStoreDetail,
  type PlatformTenant,
  type PlatformTenantDetail,
  type PlatformWatch,
  type ReleaseChannel,
  type ReleasesResponse,
  type RetentionCohort,
  type WatchStore,
} from "../api";
import { ago, dateTime, dayName, isStale, money } from "../format";
import { BarChart, BarList, Card, ConfirmButton, EmptyState, Icon, LoadError, Notice, PageHead, Segmented, Skeleton, SkeletonRows, Stat } from "../ui";
import { meterNames, PlatformUsageCard } from "./UsageCard";
import { PlatformBillingCard } from "./BillingPage";
import { PlatformReferralsCard } from "./ReferralCards";

/** Plataforma (TK-189 / TK-190): Abastalia staff only. Same Escuadra Admin pieces as the owner's panel. */

export const stageNames: Record<FunnelStage, string> = {
  account: "Solo la cuenta",
  store: "Tienda creada",
  connected: "Caja conectada",
  firstSale: "Primera venta",
  sevenDays: "7 días con ventas",
  certified: "Certificado DGII",
};

const featureNames: Record<string, string> = {
  onAccount: "Fiado",
  multipleRegisters: "Más de una caja",
  ecfLive: "e-CF en producción",
  scale: "Balanza",
  printer: "Impresora",
};

/** TK-196: "Prueba · quedan 5 días", "Restringida", "Activa · vence 12 nov."; "—" from versions that do not report it. */
export function licenseLabel(status: string | null | undefined, endsUtc: string | null | undefined, now = Date.now()) {
  if (!status) return "—";
  if (status === "Trial") {
    if (!endsUtc) return "Prueba · sin ventas aún";
    const days = Math.ceil((Date.parse(endsUtc) - now) / 86_400_000);
    return `Prueba · quedan ${days} ${days === 1 ? "día" : "días"}`;
  }
  const names: Record<string, string> = { Active: "Activa", Grace: "En gracia", Restricted: "Restringida", Blocked: "Bloqueada" };
  return status === "Restricted" || status === "Blocked" || !endsUtc ? (names[status] ?? status) : `${names[status] ?? status} · vence ${dayName(endsUtc.slice(0, 10))}`;
}

/** TK-238: the DGII environment the store sends to now (heartbeat); "Dgii" and "Relay" come from older versions. */
const fiscalModes: Record<string, string> = {
  Simulated: "simulada",
  TesteCF: "pruebas de la DGII (TesteCF)",
  CerteCF: "certificación de la DGII (CerteCF)",
  eCF: "producción (DGII)",
  Dgii: "DGII",
  Relay: "por la nube (DGII)",
};

const count = (n: number) => n.toLocaleString("en-US");
/** "50 %"; "—" when nothing reports it (NaN). */
const percent = (n: number) => (Number.isNaN(n) ? "—" : `${Math.round(n)} %`);
const share = (part: number, whole: number) => (whole === 0 ? 0 : (part / whole) * 100);

/** "Sin conexión", "La nube respondió 503": the heartbeat sends the kind of error, never its text. */
export const errorName = (code: string) =>
  code === "unreachable" ? "Sin conexión" : code.startsWith("http_") ? `La nube respondió ${code.slice(5)}` : "Otro error";

function PlatformTabs() {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const tabs: [string, string][] = [
    ["/app/platform", "Métricas"],
    ["/app/platform/tenants", "Clientes"],
    ["/app/platform/accompaniment", "Acompañamiento"],
    ["/app/platform/delegations", "Firmantes"],
    ["/app/platform/releases", "Versiones"],
    ["/app/platform/health", "Salud"],
  ];
  const current =
    pathname === "/app/platform" ? "/app/platform"
    : pathname.startsWith("/app/platform/accompaniment") ? "/app/platform/accompaniment"
    : pathname.startsWith("/app/platform/delegations") ? "/app/platform/delegations"
    : pathname.startsWith("/app/platform/releases") ? "/app/platform/releases"
    : pathname.startsWith("/app/platform/health") ? "/app/platform/health"
    : "/app/platform/tenants";
  return (
    <div className="esc-tabs" role="tablist" aria-label="Plataforma">
      {tabs.map(([to, label]) => (
        // TK-311: on a phone the strip scrolls; the open tab is brought into view.
        <button key={to} type="button" role="tab" aria-selected={to === current} onClick={() => navigate(to)}
          ref={to === current ? (b) => b?.scrollIntoView?.({ block: "nearest", inline: "nearest" }) : undefined}>
          {label}
        </button>
      ))}
    </div>
  );
}

const staffNote = "Solo para el equipo de Abastalia. Cada consulta queda registrada.";

/** R11 B-Med6: the console needs two-step verification on, and lasts one access token (30 minutes) after the sign-in with the code. */
function PlatformError({ error }: { error?: ApiError }) {
  if (error?.status !== 403) return <LoadError error={error} />;
  return (
    <Notice kind="warning" title="Vuelva a entrar con su código de verificación.">
      La plataforma pide la verificación en dos pasos (actívela en <Link to="/app/security">Seguridad</Link>) y se cierra 30 minutos después de entrar con el
      código. Salga y entre de nuevo.
    </Notice>
  );
}

export function PlatformMetricsPage() {
  const overview = useApi<MetricsOverview>("/api/platform/metrics/overview");
  const daily = useApi<DailyActivity[]>("/api/platform/metrics/daily?days=30");
  const retention = useApi<RetentionCohort[]>("/api/platform/metrics/retention?weeks=8");
  const o = overview.data;

  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote} />
      <PlatformTabs />
      <PlatformError error={overview.error} />
      {!o && overview.loading && <Skeleton height={120} />}
      {o && (
        <>
          <section className="esc-card" aria-label="Indicadores">
            <div className="esc-stats platform-stats">
              <Stat icon="store" label="Tiendas activas hoy" value={count(o.activeToday)} note={`de ${count(o.stores)} tiendas`} />
              <Stat icon="calendar" label="Activas en 7 días" value={count(o.activeLast7Days)} note={`${count(o.activeLast30Days)} en 30 días`} />
              <Stat icon="receipt" label="Ventas en 30 días" value={money(o.amountLast30Days)} note={`${count(o.salesLast30Days)} facturas`} />
              <Stat icon="users" label="Clientes" value={count(o.tenants)} note={`${count(o.funnel.find((f) => f.stage === "sevenDays")?.tenants ?? 0)} ya vendieron 7 días`} />
              <Stat icon="cloud" label="Cajas en línea" value={`${count(o.nodesOnline)} de ${count(o.nodes)}`} note="Enviaron datos en 24 h" />
              <Stat icon="alert" label="Datos sin procesar" value={count(o.health.failedEvents)} note="Fallaron 10 veces" />
            </div>
          </section>

          <div className="esc-grid">
            <Card title="Tiendas activas por día" aside="Últimos 30 días" className="esc-span-8">
              <BarChart
                label="Tiendas activas por día"
                emptyTitle="Ninguna tienda ha vendido"
                empty="Una tienda cuenta como activa el día que vende."
                format={count}
                points={(daily.data ?? []).map((d, i, all) => ({
                  key: d.day,
                  label: dayName(d.day).split(",")[1]?.trim().split(" ")[0] ?? d.day.slice(8),
                  title: capitalize(dayName(d.day)),
                  value: d.activeStores,
                  detail: `${count(d.sales)} facturas · ${money(d.amount)}`,
                  current: i === all.length - 1,
                }))}
              />
            </Card>

            <Card title="Embudo de activación" aside="Clientes" className="esc-span-4">
              <BarList
                format={count}
                items={o.funnel.map((f) => ({
                  key: f.stage,
                  name: stageNames[f.stage],
                  detail: percent(share(f.tenants, o.funnel[0].tenants)),
                  value: f.tenants,
                  share: share(f.tenants, o.funnel[0].tenants) / 100,
                }))}
              />
            </Card>

            <Card title="Uso de funciones" aside="Tiendas con latido en 7 días" className="esc-span-6">
              <BarList
                format={percent}
                items={o.features.map((f) => ({
                  key: f.feature,
                  name: featureNames[f.feature] ?? f.feature,
                  detail: f.reporting === 0 ? "sin datos" : `${f.stores} de ${f.reporting}`,
                  value: f.reporting === 0 ? NaN : share(f.stores, f.reporting),
                  share: share(f.stores, f.reporting) / 100,
                }))}
              />
            </Card>

            <Card title="Salud" className="esc-span-6">
              <ul className="platform-kv">
                <Kv label="Cajas sin datos en 24 h" value={count(o.health.staleNodes)} warn={o.health.staleNodes > 0} />
                <Kv label="Cajas con error al subir" value={count(o.health.nodesWithErrors)} warn={o.health.nodesWithErrors > 0} />
                <Kv label="Cajas con más de 500 datos por subir" value={count(o.health.nodesWithBacklog)} warn={o.health.nodesWithBacklog > 0} />
                <Kv label="Datos recibidos que fallaron" value={count(o.health.failedEvents)} warn={o.health.failedEvents > 0} />
              </ul>
              <h3 className="platform-subtitle">Versiones en las tiendas</h3>
              {o.health.versions.length === 0 ? (
                <p className="esc-muted">Ninguna caja ha enviado su latido en los últimos 7 días.</p>
              ) : (
                <BarList
                  format={count}
                  items={o.health.versions.map((v) => ({ key: v.version, name: v.version, value: v.nodes, share: v.nodes / Math.max(1, ...o.health.versions.map((x) => x.nodes)) }))}
                />
              )}
            </Card>

            <Card title="Retención por semana de registro" aside="Clientes con alguna tienda activa esa semana" className="esc-span-12">
              <Retention cohorts={retention.data} />
            </Card>

            <Card title="Señales de abandono" aside={`${o.churn.length} ${o.churn.length === 1 ? "tienda" : "tiendas"}`} className="esc-span-12">
              {o.churn.length === 0 ? (
                <EmptyState icon="check" title="Ninguna tienda en riesgo">
                  Aquí aparecen las tiendas que dejaron de vender 7 días o cuya caja no envía datos hace 3 días.
                </EmptyState>
              ) : (
                <ul className="esc-alerts">
                  {o.churn.map((c) => (
                    <li key={c.storeId} className="esc-alert esc-alert--warning">
                      <span className="esc-alert__icon">
                        <Icon name={c.kind === "Disconnected" ? "cloud" : "receipt"} size={18} />
                      </span>
                      <div className="esc-alert__body">
                        <div className="esc-alert__title">
                          {c.storeName}: {c.kind === "Disconnected" ? "la caja no envía datos" : "dejó de vender"}
                        </div>
                        <div className="esc-alert__meta">
                          {[c.tenantName, c.lastSaleDay ? `última venta ${dayName(c.lastSaleDay)}` : "nunca vendió", c.lastSyncUtc ? `último envío ${ago(c.lastSyncUtc)}` : null]
                            .filter(Boolean)
                            .join(" · ")}
                        </div>
                      </div>
                      <Link className="esc-button esc-button--small" to={`/app/platform/stores/${c.storeId}`}>
                        Ver tienda <Icon name="chevron-right" size={16} />
                      </Link>
                    </li>
                  ))}
                </ul>
              )}
            </Card>

            <Card title="Certificación DGII" aside={`${o.certification.filter((c) => c.stuck).length} detenidas`} className="esc-span-12">
              <Certification rows={o.certification} />
            </Card>
          </div>
        </>
      )}
    </>
  );
}

/** TK-225: each store in the DGII certification (from the store's assistant), stuck ones first. */
function Certification({ rows }: { rows: CertificationProgress[] }) {
  if (rows.length === 0)
    return (
      <EmptyState icon="shield" title="Ninguna tienda en certificación">
        Aparecen cuando una tienda marca su primer paso en el asistente de certificación.
      </EmptyState>
    );
  return (
    <div className="esc-table-wrap">
      <table className="esc-table">
        <thead>
          <tr>
            <th>Tienda</th>
            <th>Paso</th>
            <th className="esc-num">Días en el paso</th>
            <th>Estado</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((c) => (
            <tr key={c.storeId}>
              <td>
                <Link to={`/app/platform/stores/${c.storeId}`}>{c.storeName}</Link>
                <span className="esc-table__sub">{c.tenantName}</span>
              </td>
              <td>{stepLabel(c)}</td>
              <td className="esc-num">{c.certified ? "—" : count(c.daysInStep)}</td>
              <td>
                <span className={`esc-pill esc-pill--${c.certified ? "success" : c.stuck ? "warning" : "neutral"}`}>
                  {c.certified ? "Certificada" : c.stuck ? "Detenida" : "Avanzando"}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const stepLabel = (c: CertificationProgress) =>
  c.certified ? "Completa" : `${Math.min(c.currentStep + 1, c.totalSteps)} de ${c.totalSteps} (${c.stepsDone} ${c.stepsDone === 1 ? "listo" : "listos"})`;

function Kv({ label, value, warn = false }: { label: string; value: ReactNode; warn?: boolean }) {
  return (
    <li>
      <span>{label}</span>
      <b className={warn ? "platform-warn" : undefined}>{value}</b>
    </li>
  );
}

/** Cohort table: one row per signup week, one column per week after it; darker = more of the cohort active. */
function Retention({ cohorts }: { cohorts?: RetentionCohort[] }) {
  if (!cohorts) return <Skeleton height={160} />;
  const width = Math.max(...cohorts.map((c) => c.activeByWeek.length));
  return (
    <div className="esc-table-wrap">
      <table className="esc-table platform-cohorts">
        <thead>
          <tr>
            <th>Semana</th>
            <th className="esc-num">Clientes</th>
            {Array.from({ length: width }, (_, k) => (
              <th key={k} className="esc-num">
                {k === 0 ? "Sem. 0" : `+${k}`}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {cohorts.map((c) => (
            <tr key={c.week}>
              <td>{dayName(c.week).split(",")[1]?.trim() ?? c.week}</td>
              <td className="esc-num">{c.tenants}</td>
              {Array.from({ length: width }, (_, k) => {
                const active = c.activeByWeek[k];
                if (active === undefined || c.tenants === 0) return <td key={k} />;
                const p = share(active, c.tenants);
                return (
                  <td key={k} className="esc-num platform-cohort" style={{ background: `color-mix(in srgb, var(--data-1) ${Math.round(p * 0.85)}%, transparent)`, color: p > 55 ? "#fff" : undefined }} title={`${active} de ${c.tenants}`}>
                    {percent(p)}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function PlatformTenantsPage() {
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const all = useApi<PlatformTenant[]>(query ? null : "/api/platform/tenants");
  // R11 B-Low1: the search (often an e-mail) goes in the body, never in a URL that telemetry keeps.
  const [found, setFound] = useState<{ data?: PlatformTenant[]; error?: ApiError; loading: boolean }>({ loading: false });
  useEffect(() => {
    if (!query) return;
    let live = true;
    setFound({ loading: true });
    api<PlatformTenant[]>("/api/platform/tenants/search", "POST", { search: query }).then(
      (data) => live && setFound({ data, loading: false }),
      (error) => live && setFound({ error: error instanceof ApiError ? error : undefined, loading: false }),
    );
    return () => {
      live = false;
    };
  }, [query]);
  const { data, error, loading } = query ? found : all;
  const navigate = useNavigate();

  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote} />
      <PlatformTabs />
      <form className="esc-row" style={{ flexWrap: "nowrap" }} role="search" onSubmit={(e) => (e.preventDefault(), setQuery(search.trim()))}>
        <label className="esc-search">
          <Icon name="search" size={18} />
          <span className="esc-visually-hidden">Buscar clientes</span>
          <input placeholder="Correo, tienda, caja o id…" value={search} onChange={(e) => setSearch(e.target.value)} />
        </label>
        <button type="submit" className="esc-button">
          Buscar
        </button>
      </form>
      <PlatformError error={error} />
      <section className="esc-card esc-table-wrap">
        <table className="esc-table esc-table--wide platform-wrap">
          <thead>
            <tr>
              <th>Cliente</th>
              <th className="esc-hide-sm">Etapa</th>
              <th className="esc-num esc-hide-sm">Tiendas</th>
              <th className="esc-num esc-hide-sm">Cajas</th>
              <th className="esc-hide-sm">Último envío</th>
              <th className="esc-hide-sm">Versión</th>
              <th className="esc-hide-sm">Licencia</th>
              <th className="esc-table__chev" aria-hidden="true" />
            </tr>
          </thead>
          <tbody>
            {loading && !data && <SkeletonRows columns={7} />}
            {data?.map((t) => (
              <tr key={t.id} className="selectable" tabIndex={0} onClick={() => navigate(`/app/platform/tenants/${t.id}`)} onKeyDown={(e) => e.key === "Enter" && navigate(`/app/platform/tenants/${t.id}`)}>
                <td>
                  <Link to={`/app/platform/tenants/${t.id}`} onClick={(e) => e.stopPropagation()}>
                    {t.ownerEmail ?? t.name}
                  </Link>
                  <span className="esc-table__sub">Desde {dayName(t.createdUtc.slice(0, 10))}</span>
                  <span className="esc-table__sub esc-only-sm">
                    {[t.disabledUtc ? "Desactivado" : stageNames[t.stage], `${t.stores} ${t.stores === 1 ? "tienda" : "tiendas"}`, t.lastActivityUtc ? `envió ${ago(t.lastActivityUtc)}` : "sin envíos"].join(" · ")}
                  </span>
                </td>
                <td className="esc-hide-sm">{t.disabledUtc ? <span className="esc-pill esc-pill--error">Desactivado</span> : <StagePill stage={t.stage} />}</td>
                <td className="esc-num esc-hide-sm">{t.stores}</td>
                <td className="esc-num esc-hide-sm">{t.nodes}</td>
                <td className="esc-hide-sm">{t.lastActivityUtc ? <SyncPill utc={t.lastActivityUtc} /> : <span className="esc-muted">Nunca</span>}</td>
                <td className="esc-hide-sm">{t.appVersion ?? "—"}</td>
                <td className="esc-hide-sm">{licenseLabel(t.licenseStatus, t.licenseEndsUtc)}</td>
                <td className="esc-table__chev">
                  <Icon name="chevron-right" size={18} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {data?.length === 0 && <EmptyState icon="users" title={query ? "Ningún cliente coincide" : "Todavía no hay clientes"} />}
      </section>
    </>
  );
}

function StagePill({ stage }: { stage: FunnelStage }) {
  const kind = stage === "sevenDays" || stage === "certified" ? "success" : stage === "firstSale" ? "brand" : "neutral";
  return <span className={`esc-pill esc-pill--${kind}`}>{stageNames[stage]}</span>;
}

function SyncPill({ utc }: { utc: string | null }) {
  return <span className={`esc-pill esc-pill--${isStale(utc) ? "warning" : "success"}`}>{utc ? ago(utc) : "Nunca"}</span>;
}

export function PlatformTenantPage() {
  const { tenantId } = useParams();
  const { data: t, error, loading, reload } = useApi<PlatformTenantDetail>(`/api/platform/tenants/${tenantId}`);

  return (
    <>
      <PageHead
        title={t?.members.find((m) => m.role === "Owner")?.email ?? t?.name ?? "Cliente"}
        subtitle={
          <>
            <Link to="/app/platform/tenants">Clientes</Link>
            {t && ` · cliente desde ${dayName(t.createdUtc.slice(0, 10))}`}
          </>
        }
      >
        {t &&
          (t.disabledUtc ? (
            <ConfirmButton label="Activar" confirm="Sus cajas vuelven a subir datos y el dueño puede entrar." onConfirm={() => api(`/api/platform/tenants/${t.id}/enable`, "POST").then(reload)} />
          ) : (
            <ConfirmButton
              label="Desactivar"
              danger
              confirm="Sus cajas dejan de subir datos (no se pierden) y el dueño no puede entrar."
              onConfirm={() => api(`/api/platform/tenants/${t.id}/disable`, "POST").then(reload)}
            />
          ))}
      </PageHead>
      <PlatformError error={error} />
      {loading && !t && <Skeleton height={120} />}
      {t && (
        <>
          {t.disabledUtc && <Notice kind="error" title={`Cliente desactivado ${ago(t.disabledUtc)}.`}>Sus cajas no pueden subir datos y sus usuarios no pueden entrar al panel.</Notice>}
          <section className="esc-card" aria-label="Indicadores del cliente">
            <div className="esc-stats platform-stats platform-stats--4">
              <Stat icon="chart" label="Etapa" value={stageNames[t.metrics.stage]} />
              <Stat icon="calendar" label="Días con ventas" value={count(t.metrics.daysWithSalesLast30)} note="Últimos 30 días" />
              <Stat icon="receipt" label="Ventas en 30 días" value={money(t.metrics.amountLast30Days)} note={`${count(t.metrics.salesLast30Days)} facturas`} />
              <Stat icon="users" label="Miembros del panel" value={count(t.members.length)} note={`${count(t.members.filter((m) => m.role === "Owner").length)} dueños`} />
            </div>
          </section>

          <PlatformUsageCard tenantId={t.id} />
          <PlatformBillingCard tenantId={t.id} />
          <PlatformReferralsCard tenantId={t.id} />

          {/* TK-217 */}
          <Card title="Miembros del panel">
            <ul className="platform-kv" aria-label="Miembros del panel">
              {t.members.map((m, n) => (
                <li key={n}>
                  <span style={{ overflowWrap: "anywhere" }}>{m.email ?? "Sin correo"}</span>
                  <span className="esc-muted">
                    {roleNames[m.role]} · desde {dayName(m.joinedUtc.slice(0, 10))}
                  </span>
                </li>
              ))}
            </ul>
          </Card>

          {t.issues.length > 0 && (
            <section className="esc-card" aria-labelledby="tenant-issues">
              <div className="esc-card__head" style={{ padding: "16px 20px 0", margin: 0 }}>
                <h2 id="tenant-issues" className="esc-card__title">
                  Para revisar
                </h2>
              </div>
              <ul className="esc-alerts">
                {t.issues.map((i, n) => (
                  <li key={n} className={`esc-alert esc-alert--${i.kind === "ShiftDifference" ? "error" : "warning"}`}>
                    <span className="esc-alert__icon">
                      <Icon name={i.kind === "ShiftDifference" ? "cash" : "cloud"} size={18} />
                    </span>
                    <div className="esc-alert__body">
                      <div className="esc-alert__title">{i.message}</div>
                      <div className="esc-alert__meta">{[i.storeName, i.cashierName, i.occurredUtc ? dateTime(i.occurredUtc) : null].filter(Boolean).join(" · ")}</div>
                    </div>
                  </li>
                ))}
              </ul>
            </section>
          )}

          {t.stores.length === 0 && (
            <div className="esc-card">
              <EmptyState icon="store" title="Todavía no creó una tienda" />
            </div>
          )}
          {t.stores.map((s) => (
            <section key={s.id} className="esc-card esc-card--padded store">
              <div className="store__head">
                <span className="store__icon">
                  <Icon name="store" />
                </span>
                <div style={{ flex: "1 1 auto", minWidth: 0 }}>
                  <h2 className="store__name">
                    <Link to={`/app/platform/stores/${s.id}`}>{s.name}</Link>
                  </h2>
                  <span className="esc-muted">
                    {s.registers} {s.registers === 1 ? "caja" : "cajas"} · {s.users} {s.users === 1 ? "usuario" : "usuarios"} ·{" "}
                    {s.firstSaleDay ? `vende desde ${dayName(s.firstSaleDay)}, última ${dayName(s.lastSaleDay!)}` : "sin ventas todavía"}
                  </span>
                </div>
                <Link className="esc-button esc-button--small" to={`/app/platform/stores/${s.id}`}>
                  Ver tienda <Icon name="chevron-right" size={16} />
                </Link>
              </div>
              <Nodes nodes={s.nodes} onChange={reload} />
            </section>
          ))}

          <Card title="Códigos de conexión" aside="Los últimos 20 (sin el código)">
            {t.connectionCodes.length === 0 ? (
              <p className="esc-muted">No ha pedido ningún código.</p>
            ) : (
              <ul className="platform-kv">
                {t.connectionCodes.map((c) => (
                  <Kv
                    key={c.issuedUtc + c.storeId}
                    label={`${t.stores.find((s) => s.id === c.storeId)?.name ?? "Tienda"} · ${dateTime(c.issuedUtc)}`}
                    value={c.usedUtc ? `Usado ${ago(c.usedUtc)}` : Date.parse(c.expiresUtc) < Date.now() ? "Venció sin usarse" : "Vigente"}
                  />
                ))}
              </ul>
            )}
          </Card>
        </>
      )}
    </>
  );
}

/** The store's PCs (nodes): version, last batch, what waits and the last upload error. Revoking asks first. */
function Nodes({ nodes, onChange }: { nodes: PlatformNode[]; onChange: () => void }) {
  if (nodes.length === 0) return <p className="esc-muted">Ninguna caja conectada.</p>;
  return (
    <ul className="nodes">
      {nodes.map((n) => (
        <li key={n.id} style={{ flexWrap: "wrap", padding: "8px 0" }}>
          <span className="esc-avatar" aria-hidden="true" style={{ background: "var(--surface-2)", color: "var(--text-2)" }}>
            <Icon name="register" size={16} />
          </span>
          <span style={{ flex: "1 1 220px", minWidth: 0 }}>
            <b>{n.name}</b>
            <span className="esc-table__sub">
              {[
                n.appVersion ? `Versión ${n.appVersion}` : "Sin latido todavía",
                n.os,
                n.outboxBacklog ? `${count(n.outboxBacklog)} por subir` : null,
                n.pendingEvents ? `${count(n.pendingEvents)} por procesar` : null,
                n.failedEvents ? `${count(n.failedEvents)} fallidos` : null,
                n.lastErrorCode ? errorName(n.lastErrorCode) : null,
              ]
                .filter(Boolean)
                .join(" · ")}
            </span>
          </span>
          {n.revokedUtc ? (
            <span className="esc-pill esc-pill--error">Revocada {ago(n.revokedUtc)}</span>
          ) : (
            <>
              <SyncPill utc={n.lastSyncUtc} />
              <ConfirmButton label="Revocar" danger confirm="Deja de subir datos; la tienda debe conectarse con un código nuevo." onConfirm={() => api(`/api/platform/nodes/${n.id}/revoke`, "POST").then(onChange)} />
            </>
          )}
        </li>
      ))}
    </ul>
  );
}

export function PlatformStorePage() {
  const { storeId } = useParams();
  const { data: s, error, loading, reload } = useApi<PlatformStoreDetail>(`/api/platform/stores/${storeId}`);
  const c = s?.checklist;
  const maxDay = Math.max(1, ...(s?.days ?? []).map((d) => d.sales));

  return (
    <>
      <PageHead
        title={s?.name ?? "Tienda"}
        subtitle={
          s && (
            <>
              <Link to={`/app/platform/tenants/${s.tenantId}`}>{s.tenantName}</Link> · creada {dayName(s.createdUtc.slice(0, 10))}
            </>
          )
        }
      />
      <PlatformError error={error} />
      {loading && !s && <Skeleton height={120} />}
      {s && c && (
        <div className="esc-grid">
          <Card title="Puesta en marcha" className="esc-span-6">
            <ul className="platform-checklist">
              <Check ok={c.connected} label="Caja conectada a la nube" />
              <Check ok={c.heartbeatReceived} label="Latido recibido" note={c.heartbeatReceived ? undefined : "Las versiones anteriores no lo envían"} />
              <Check ok={c.businessNameSet} label="Datos del negocio guardados" />
              <Check ok={c.rncSet} label="RNC configurado" />
              <Check ok={c.sequencesLoaded} label="Secuencias e-NCF cargadas" note="Se sabe cuando emite su primer comprobante" />
              <Check ok={c.activeProducts === null ? null : c.activeProducts > 0} label="Productos" note={c.activeProducts === null ? undefined : `${count(c.activeProducts)} activos`} />
              <Check ok={c.firstSaleDay !== null} label="Primera venta" note={c.firstSaleDay ? dayName(c.firstSaleDay) : undefined} />
            </ul>
            <p className="esc-muted" style={{ marginTop: 12 }}>
              Facturación electrónica: {c.fiscalProvider ? (fiscalModes[c.fiscalProvider] ?? c.fiscalProvider) : "sin datos"}
            </p>
          </Card>

          <Card title="Funciones" aside={s.features ? undefined : "Sin latido"} className="esc-span-6">
            {s.features ? (
              <ul className="platform-kv">
                <Kv label="Licencia" value={licenseLabel(s.features.licenseStatus, s.features.licenseEndsUtc)} />
                <Kv label="Cajas con turnos (30 días)" value={count(s.features.activeRegisters)} />
                <Kv label="Turnos abiertos ahora" value={count(s.features.openShifts)} />
                <Kv label="Fiado" value={s.features.onAccount ? "Lo usa" : "No"} />
                <Kv label="Balanza" value={yesNo(s.features.scaleConfigured)} />
                <Kv label="Impresora" value={yesNo(s.features.printerConfigured)} />
              </ul>
            ) : (
              <p className="esc-muted">Cuando la caja envíe su latido (cada hora) aparecen aquí sus funciones.</p>
            )}
          </Card>

          {s.certification && (
            <Card title="Certificación DGII" className="esc-span-12">
              <ul className="platform-kv">
                <Kv label="Paso" value={stepLabel(s.certification)} />
                {!s.certification.certified && (
                  <Kv label="En este paso desde" value={`${dayName(s.certification.stepSinceUtc.slice(0, 10))} (${count(s.certification.daysInStep)} días)`} warn={s.certification.stuck} />
                )}
              </ul>
            </Card>
          )}

          <Card title="Cajas (PC principal)" className="esc-span-12">
            <Nodes nodes={s.nodes} onChange={reload} />
          </Card>

          <Card title="Últimos 14 días" aside="Réplica y lo que contó la tienda" className="esc-span-12">
            <div className="esc-table-wrap">
              <table className="esc-table">
                <thead>
                  <tr>
                    <th>Día</th>
                    <th className="esc-num">Facturas</th>
                    <th className="esc-num">Vendido</th>
                    <th className="esc-num esc-hide-sm">Según la tienda</th>
                    <th className="esc-num esc-hide-sm">Cajeros</th>
                    <th className="esc-num esc-hide-sm">Anuladas</th>
                  </tr>
                </thead>
                <tbody>
                  {s.days.map((d) => (
                    <tr key={d.day}>
                      <td>
                        {capitalize(dayName(d.day))}
                        <div className="platform-meter" aria-hidden="true">
                          <div style={{ width: `${(d.sales / maxDay) * 100}%` }} />
                        </div>
                      </td>
                      <td className="esc-num">{count(d.sales)}</td>
                      <td className="esc-num">{money(d.amount)}</td>
                      <td className="esc-num esc-hide-sm">{d.reportedSales ?? "—"}</td>
                      <td className="esc-num esc-hide-sm">{d.activeCashiers ?? "—"}</td>
                      <td className="esc-num esc-hide-sm">{d.voids ?? "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Card>
        </div>
      )}
    </>
  );
}

function Check({ ok, label, note }: { ok: boolean | null; label: string; note?: string }) {
  const kind = ok === null ? "unknown" : ok ? "ok" : "missing";
  return (
    <li className={`platform-check platform-check--${kind}`}>
      <span className="platform-check__mark" aria-hidden="true">
        <Icon name={ok ? "check" : ok === null ? "info" : "alert"} size={16} />
      </span>
      <span>
        {label}
        <span className="esc-visually-hidden">: {ok === null ? "sin datos" : ok ? "listo" : "pendiente"}</span>
        {note && <span className="esc-table__sub">{note}</span>}
      </span>
    </li>
  );
}

const yesNo = (v: boolean | null) => (v === null ? "Sin datos" : v ? "Configurada" : "No");
const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

/** TK-234: the accompaniment requests (TK-233), open ones first. */
export const accompanimentNames: Record<AccompanimentStatus, string> = {
  New: "Nueva",
  Scheduled: "Agendada",
  Session1Done: "Sesión 1 hecha",
  Session2Done: "Sesión 2 hecha",
  Certified: "Certificada",
  Cancelled: "Cancelada",
};

const accompanimentPill: Record<AccompanimentStatus, string> = {
  New: "warning",
  Scheduled: "brand",
  Session1Done: "brand",
  Session2Done: "brand",
  Certified: "success",
  Cancelled: "neutral",
};

/** Staff type the store's time (UTC−4, no daylight saving) in a datetime-local box; the API keeps UTC. */
const storeOffsetMs = 4 * 60 * 60 * 1000;
export const toStoreInput = (utc: string | null) => (utc ? new Date(Date.parse(utc) - storeOffsetMs).toISOString().slice(0, 16) : "");
export const fromStoreInput = (local: string) => (local ? new Date(Date.parse(local + ":00Z") + storeOffsetMs).toISOString() : null);

export function PlatformAccompanimentPage() {
  const { data, error, loading, reload } = useApi<PlatformAccompaniment[]>("/api/platform/accompaniment");
  const [editing, setEditing] = useState<string | null>(null);

  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote} />
      <PlatformTabs />
      <PlatformError error={error} />
      <section className="esc-card esc-table-wrap" aria-label="Solicitudes de acompañamiento">
        <table className="esc-table esc-table--wide platform-wrap">
          <thead>
            <tr>
              <th>Tienda</th>
              <th>Contacto</th>
              <th>Estado</th>
              <th className="esc-hide-sm">Citas</th>
              <th className="esc-table__chev" aria-hidden="true" />
            </tr>
          </thead>
          <tbody>
            {loading && !data && <SkeletonRows columns={4} />}
            {data?.map((r) => (
              <AccompanimentRow key={r.id} request={r} editing={editing === r.id} onEdit={() => setEditing(editing === r.id ? null : r.id)} onSaved={() => (setEditing(null), reload())} />
            ))}
          </tbody>
        </table>
        {data?.length === 0 && (
          <EmptyState icon="shield" title="Ninguna solicitud todavía">
            Aparecen cuando el dueño toca "Solicitar acompañamiento" en Comprobantes electrónicos.
          </EmptyState>
        )}
      </section>
    </>
  );
}

export const channelNames: Record<ReleaseChannel, string> = { pilot: "Piloto", stable: "Estable" };

/** TK-254: publish a version per channel with a staged rollout, and see which versions run in the field (from the heartbeats).
 * The files and their signature stay in the release feed; this only decides which stores may install them. */
export function PlatformReleasesPage() {
  const { data, error, loading, reload } = useApi<ReleasesResponse>("/api/platform/releases");
  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote} />
      <PlatformTabs />
      <LoadError error={error} />
      <div className="esc-grid">
        <Card title="Publicar o cambiar el porcentaje" className="esc-span-8">
          <ReleaseForm onSaved={reload} />
        </Card>
        <Card title="Versiones en las tiendas" aside="Último latido" className="esc-span-4">
          {loading && !data && <Skeleton height={120} />}
          {data && data.field.length === 0 && <p className="esc-muted">Ninguna caja ha enviado un latido todavía.</p>}
          {data && data.field.length > 0 && (
            <BarList
              format={count}
              items={data.field.map((v) => ({
                key: v.version,
                name: v.version === "?" ? "Sin versión" : `Versión ${v.version}`,
                detail: `${count(v.stores)} ${v.stores === 1 ? "tienda" : "tiendas"}`,
                value: v.nodes,
                share: v.nodes / Math.max(...data.field.map((f) => f.nodes)),
              }))}
            />
          )}
        </Card>
      </div>
      <section className="esc-card esc-table-wrap" aria-label="Versiones publicadas">
        <table className="esc-table esc-table--wide">
          <thead>
            <tr>
              <th>Versión</th>
              <th>Canal</th>
              <th className="esc-num">Tiendas</th>
              <th className="esc-hide-sm">Último cambio</th>
            </tr>
          </thead>
          <tbody>
            {loading && !data && <SkeletonRows columns={4} />}
            {data?.releases.map((r) => (
              <tr key={r.id}>
                <td>
                  {r.version}
                  {r.notesUrl && (
                    <span className="esc-table__sub">
                      <a href={r.notesUrl} target="_blank" rel="noopener noreferrer">
                        Novedades
                      </a>
                    </span>
                  )}
                </td>
                <td>{channelNames[r.channel]}</td>
                <td className="esc-num">{r.rolloutPercent === 0 ? "En pausa" : `${r.rolloutPercent} %`}</td>
                <td className="esc-hide-sm">
                  {dateTime(r.changedUtc)}
                  <span className="esc-table__sub">{r.changedBy}</span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {data?.releases.length === 0 && (
          <EmptyState icon="cloud" title="Ninguna versión publicada">
            Sin versiones aquí, cada tienda instala la más nueva de su canal en el feed, como antes.
          </EmptyState>
        )}
      </section>
    </>
  );
}

function ReleaseForm({ onSaved }: { onSaved: () => void }) {
  const [channel, setChannel] = useState<ReleaseChannel>("pilot");
  const [version, setVersion] = useState("");
  const [percent, setPercent] = useState("10");
  const [notesUrl, setNotesUrl] = useState("");
  const [failure, setFailure] = useState<ApiError | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const save = (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setFailure(null);
    api("/api/platform/releases", "PUT", { channel, version: version.trim(), rolloutPercent: Number(percent), notesUrl: notesUrl.trim() || null }).then(
      () => (setSaving(false), setSaved(`Versión ${version.trim()} en ${channelNames[channel].toLowerCase()} al ${percent} %.`), onSaved()),
      (err) => (setSaving(false), setFailure(err as ApiError)),
    );
  };

  return (
    <form onSubmit={save} aria-label="Publicar una versión">
      {failure && <Notice kind="error" title={failure.detail} />}
      {saved && <Notice kind="success" title={saved} />}
      <div className="esc-form-grid">
        <label className="esc-field">
          <span>Canal</span>
          <select className="esc-select" value={channel} onChange={(e) => setChannel(e.target.value as ReleaseChannel)}>
            <option value="pilot">Piloto</option>
            <option value="stable">Estable</option>
          </select>
        </label>
        <label className="esc-field">
          <span>Versión</span>
          <input className="esc-input" placeholder="1.4.0" value={version} onChange={(e) => setVersion(e.target.value)} required />
        </label>
        <label className="esc-field">
          <span>Tiendas que la reciben (%)</span>
          <input className="esc-input" type="number" min={0} max={100} value={percent} onChange={(e) => setPercent(e.target.value)} required />
        </label>
        <label className="esc-field esc-wide">
          <span>Enlace a las novedades (https, opcional)</span>
          <input className="esc-input" type="url" value={notesUrl} onChange={(e) => setNotesUrl(e.target.value)} />
        </label>
      </div>
      <p className="esc-muted">La versión tiene que estar en el feed, firmada. 0 % la pone en pausa; las mismas tiendas reciben primero cada versión.</p>
      <div className="esc-form-actions">
        <button type="submit" className="esc-button esc-button--primary" disabled={saving}>
          {saving ? "Guardando…" : "Guardar"}
        </button>
      </div>
    </form>
  );
}

function AccompanimentRow({ request: r, editing, onEdit, onSaved }: { request: PlatformAccompaniment; editing: boolean; onEdit: () => void; onSaved: () => void }) {
  return (
    <>
      <tr>
        <td>
          <Link to={`/app/platform/stores/${r.storeId}`}>{r.storeName || "Tienda"}</Link>
          <span className="esc-table__sub">{r.tenantName}</span>
          <span className="esc-table__sub">Pedida {dateTime(r.createdUtc)}</span>
        </td>
        <td>
          {r.contactName}
          <span className="esc-table__sub">{r.phone}</span>
          {r.preferredTimes && <span className="esc-table__sub">{r.preferredTimes}</span>}
        </td>
        <td>
          <span className={`esc-pill esc-pill--${accompanimentPill[r.status]}`}>{accompanimentNames[r.status]}</span>
          {r.notes && <span className="esc-table__sub">{r.notes}</span>}
        </td>
        <td className="esc-hide-sm">
          {[r.session1Utc && `Cita 1: ${dateTime(r.session1Utc)}`, r.session2Utc && `Cita 2: ${dateTime(r.session2Utc)}`].filter(Boolean).join(" · ") || "Sin agendar"}
        </td>
        <td className="esc-num">
          <button type="button" className="esc-button esc-button--small" aria-expanded={editing} onClick={onEdit}>
            {editing ? "Cerrar" : "Editar"}
          </button>
        </td>
      </tr>
      {editing && (
        <tr>
          <td colSpan={5}>
            <AccompanimentForm request={r} onSaved={onSaved} />
          </td>
        </tr>
      )}
    </>
  );
}

/** Saving a new date for a session e-mails the owner its confirmation (the first one with the preparation list). */
function AccompanimentForm({ request: r, onSaved }: { request: PlatformAccompaniment; onSaved: () => void }) {
  const [status, setStatus] = useState<AccompanimentStatus>(r.status);
  const [session1, setSession1] = useState(toStoreInput(r.session1Utc));
  const [session2, setSession2] = useState(toStoreInput(r.session2Utc));
  const [notes, setNotes] = useState(r.notes ?? "");
  const [failure, setFailure] = useState<ApiError | null>(null);
  const [saving, setSaving] = useState(false);

  const save = (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    api(`/api/platform/accompaniment/${r.id}`, "PUT", { status, session1Utc: fromStoreInput(session1), session2Utc: fromStoreInput(session2), notes }).then(
      onSaved,
      (err) => (setSaving(false), setFailure(err as ApiError)),
    );
  };

  return (
    <form onSubmit={save} aria-label={`Editar la solicitud de ${r.storeName}`}>
      {failure && <Notice kind="error" title={failure.detail} />}
      <div className="esc-form-grid">
        <label className="esc-field">
          <span>Estado</span>
          <select className="esc-select" value={status} onChange={(e) => setStatus(e.target.value as AccompanimentStatus)}>
            {(Object.keys(accompanimentNames) as AccompanimentStatus[]).map((s) => (
              <option key={s} value={s}>
                {accompanimentNames[s]}
              </option>
            ))}
          </select>
        </label>
        <label className="esc-field">
          <span>Cita 1 (hora de la tienda)</span>
          <input className="esc-input" type="datetime-local" value={session1} onChange={(e) => setSession1(e.target.value)} />
        </label>
        <label className="esc-field">
          <span>Cita 2 (hora de la tienda)</span>
          <input className="esc-input" type="datetime-local" value={session2} onChange={(e) => setSession2(e.target.value)} />
        </label>
        <label className="esc-field esc-wide">
          <span>Notas del equipo (el dueño no las ve)</span>
          <textarea className="esc-input" maxLength={2000} rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </label>
      </div>
      <p className="esc-muted">Al guardar una fecha nueva, el dueño recibe la confirmación por correo (la de la cita 1 con la lista de preparación).</p>
      <div className="esc-form-actions">
        <button type="submit" className="esc-button esc-button--primary" disabled={saving}>
          {saving ? "Guardando…" : "Guardar"}
        </button>
      </div>
    </form>
  );
}

/** R11 B-High2: receiver activations with a person's certificate (cédula) for a company's RNC. Support checks outside Abastalia (the DGII's records)
 * that the person signs for the company, then approves or refuses (audited); after an approval the store activates again. */
export function PlatformDelegationsPage() {
  const { data, error, loading, reload } = useApi<FeDelegation[]>("/api/platform/fe/delegations");
  const decide = (id: string, approve: boolean) => api(`/api/platform/fe/delegations/${id}`, "POST", { approve }).then(reload);

  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote} />
      <PlatformTabs />
      <PlatformError error={error} />
      <section className="esc-card esc-table-wrap" aria-label="Firmantes por verificar">
        <table className="esc-table esc-table--wide platform-wrap">
          <thead>
            <tr>
              <th>Cliente</th>
              <th>Firmante</th>
              <th>Estado</th>
            </tr>
          </thead>
          <tbody>
            {loading && !data && <SkeletonRows columns={3} />}
            {data?.map((d) => (
              <tr key={d.id}>
                <td>
                  <Link to={`/app/platform/tenants/${d.tenantId}`}>{d.tenantName}</Link>
                  <span className="esc-table__sub">RNC {d.rnc} · pedida {dateTime(d.requestedUtc)}</span>
                </td>
                <td>
                  {d.holderName}
                  <span className="esc-table__sub">{d.subject}</span>
                </td>
                <td>
                  {d.approved === null ? (
                    <span className="esc-row" style={{ gap: 8 }}>
                      <ConfirmButton label="Aprobar" confirm={`¿Verificó que firma por el RNC ${d.rnc}?`} onConfirm={() => decide(d.id, true)} />
                      <ConfirmButton label="Rechazar" danger confirm="¿Rechazar este firmante?" onConfirm={() => decide(d.id, false)} />
                    </span>
                  ) : (
                    <>
                      <span className={`esc-pill esc-pill--${d.approved ? "success" : "error"}`}>{d.approved ? "Aprobado" : "Rechazado"}</span>
                      <span className="esc-table__sub">{[d.decidedBy, d.decidedUtc && dateTime(d.decidedUtc)].filter(Boolean).join(" · ")}</span>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        {data?.length === 0 && (
          <EmptyState icon="shield" title="Ninguna solicitud">
            Aparecen cuando una tienda activa el receptor con el certificado de una persona para el RNC de una empresa.
          </EmptyState>
        )}
      </section>
    </>
  );
}

const reasonNames: Record<string, string> = {
  "Gate.PerIpRate": "429 · muchas por minuto desde una IP",
  "Gate.PerIpConcurrency": "429 · muchas a la vez desde una IP",
  "Gate.LengthRequired": "411 · envío sin su tamaño",
  "Gate.TooLarge": "413 · envío más pesado que el límite",
  "Gate.Compressed": "415 · respaldo comprimido",
  "Node.Unknown": "Caja desconocida",
  "Node.Revoked": "Caja revocada",
  "Node.BadSignature": "Firma de caja inválida",
  RateLimit: "429 · límite por plan o ruta",
  Http5xx: "Error del servidor (5xx)",
};

const policyNames: Record<string, string> = {
  "panel-user": "Panel, por usuario",
  "assistant-user": "Asistente, por usuario",
  auth: "Entrada y registro, por IP",
  "register-node": "Conectar caja, por IP",
  global: "Receptor de e-CF",
};

const reasonName = (reason: string) => reasonNames[reason] ?? reason;
const sum = (rows: { count: number }[]) => rows.reduce((total, r) => total + r.count, 0);

/** TK-311 (D22): the limits watched in the pilot (per-IP gate, plan and route limits, quotas, 5xx, silent stores, e-CF queue). Thresholds come
 * from the API, the same numbers the Azure alerts use. */
export function PlatformWatchPage() {
  const [days, setDays] = useState<"1" | "7">("1");
  const { data: w, error, loading } = useApi<PlatformWatch>(`/api/platform/watch?days=${days}`);
  const period = days === "1" ? "24 h" : "7 días";
  const perIp = w?.reasons.filter((r) => r.reason.startsWith("Gate.PerIp")) ?? [];
  const gate = w?.reasons.filter((r) => r.reason.startsWith("Gate.") && !r.reason.startsWith("Gate.PerIp")) ?? [];
  const nodes = w?.reasons.filter((r) => r.reason.startsWith("Node.")) ?? [];
  const limits = w?.reasons.filter((r) => r.reason === "RateLimit") ?? [];
  const errors = w?.reasons.filter((r) => r.reason === "Http5xx") ?? [];
  const refused = w?.reasons.filter((r) => r.reason !== "Http5xx") ?? [];

  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote}>
        <Segmented label="Periodo" options={[["1", "Últimas 24 h"], ["7", "Últimos 7 días"]]} value={days} onChange={setDays} />
      </PageHead>
      <PlatformTabs />
      <PlatformError error={error} />
      {!w && loading && <Skeleton height={120} />}
      {w && (
        <>
          <section className="esc-card" aria-label="Indicadores de salud">
            <div className="esc-stats platform-stats">
              <Stat icon="alert" label="429 por IP" value={count(sum(perIp))} note={`${count(perIp.reduce((t, r) => t + r.retryAfter, 0))} con Retry-After`} />
              <Stat icon="cloud" label="Envíos rechazados" value={count(sum(gate))} note="411, 413 y 415" />
              <Stat icon="shield" label="Cajas rechazadas" value={count(sum(nodes))} note="Desconocidas, revocadas o mal firmadas" />
              <Stat icon="users" label="Límites por plan o ruta" value={count(sum(limits))} note="Respuestas 429" />
              <Stat icon="x" label="Errores 5xx" value={count(sum(errors))} note={`En ${period}`} />
              <Stat icon="store" label="Tiendas sin latido" value={count(w.silentStores.length)} note={`Más de ${w.thresholds.silentStoreHours} h`} />
            </div>
          </section>

          <div className="esc-grid">
            <Card title="IPs con más rechazos" aside={`Alerta: más de ${w.thresholds.perIpRejections15Min} en 15 min o ${w.thresholds.storesBehindIp} tiendas detrás`} className="esc-span-12">
              {w.ips.length === 0 ? (
                <EmptyState icon="check" title="Ninguna IP rechazada">
                  Aquí aparece cada IP (como su red /24 y una clave, nunca la dirección) que alguno de los límites rechazó en {period}.
                </EmptyState>
              ) : (
                <div className="esc-table-wrap">
                  <table className="esc-table">
                    <thead>
                      <tr>
                        <th>Red</th>
                        <th className="esc-num">Rechazos</th>
                        <th className="esc-num">Pico de 429 en 15 min</th>
                        <th className="esc-num">Tiendas detrás</th>
                        <th className="esc-hide-sm">Motivos</th>
                      </tr>
                    </thead>
                    <tbody>
                      {w.ips.map((ip) => (
                        <tr key={ip.network + ip.ipKey}>
                          <td>
                            {ip.network}
                            <span className="esc-table__sub">clave {ip.ipKey.slice(0, 8)}</span>
                          </td>
                          <td className="esc-num">{count(ip.count)}</td>
                          <td className="esc-num">
                            <Over value={ip.peak15Min} over={ip.peak15Min > w.thresholds.perIpRejections15Min} />
                          </td>
                          <td className="esc-num">
                            <Over value={ip.stores} over={ip.stores >= w.thresholds.storesBehindIp && ip.peak15Min > 0} />
                          </td>
                          <td className="esc-hide-sm platform-wrap">{ip.reasons.map(reasonName).join(" · ")}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Card>

            <Card title="Rechazos por motivo" aside={period} className="esc-span-6">
              <ReasonTable rows={refused} empty="Ningún límite rechazó nada en este periodo." />
            </Card>

            <Card title="Errores 5xx por ruta" aside={period} className="esc-span-6">
              <ReasonTable routes rows={errors} empty="La nube no respondió ningún error 5xx en este periodo." />
            </Card>

            <Card title="Cuotas del mes cerca del límite" aside={`Desde ${w.thresholds.quotaNearPercent} %`} className="esc-span-12">
              {w.quotas.length === 0 ? (
                <EmptyState icon="check" title="Ningún negocio cerca de su límite">
                  Aparecen los negocios que usaron {w.thresholds.quotaNearPercent} % o más de algún servicio del mes.
                </EmptyState>
              ) : (
                <div className="esc-table-wrap">
                  <table className="esc-table">
                    <thead>
                      <tr>
                        <th>Cliente</th>
                        <th>Servicio</th>
                        <th className="esc-num">Usado</th>
                        <th>Estado</th>
                      </tr>
                    </thead>
                    <tbody>
                      {w.quotas.map((q) => (
                        <tr key={q.tenantId + q.meter}>
                          <td>
                            <Link to={`/app/platform/tenants/${q.tenantId}`}>{q.tenantName}</Link>
                          </td>
                          <td>{meterNames[q.meter] ?? q.meter}</td>
                          <td className="esc-num">
                            {count(q.used)} de {count(q.limit)}
                          </td>
                          <td>
                            <span className={`esc-pill esc-pill--${q.used >= q.limit ? "error" : "warning"}`}>{quotaLabel(q)}</span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Card>

            <Card title="Tiendas sin latido" aside={`Más de ${w.thresholds.silentStoreHours} h`} className="esc-span-6">
              <StoreList
                rows={w.silentStores}
                empty="Todas las tiendas conectadas enviaron su latido a tiempo."
                detail={(s) => `último latido ${ago(s.lastHeartbeatUtc)}`}
                pill={() => ["warning", "Sin latido"]}
              />
            </Card>

            <Card title="Cola de e-CF" aside={`Alerta al dueño después de ${w.thresholds.queueStaleHours} h`} className="esc-span-6">
              <StoreList
                rows={w.ecfQueues}
                empty="Ninguna tienda tiene comprobantes esperando a la DGII."
                detail={(s) => `el más viejo espera ${hours(s.queueHours ?? 0)} · latido ${ago(s.lastHeartbeatUtc)}`}
                pill={(s) =>
                  (s.queueHours ?? 0) > 72 ? ["error", "Pasó las 72 h"] : (s.queueHours ?? 0) > w.thresholds.queueStaleHours ? ["warning", "Atrasada"] : ["success", "Al día"]
                }
              />
            </Card>
          </div>
        </>
      )}
    </>
  );
}

const hours = (h: number) => (h < 1 ? "menos de 1 h" : `${Math.round(h).toLocaleString("en-US")} h`);

const quotaLabel = (q: PlatformWatch["quotas"][number]) =>
  q.used > q.limit ? (q.meter === "ReceiverSignatures" ? "Pasó el uso justo" : "Pasó el límite") : q.used === q.limit ? "En el límite" : "Cerca";

/** A number past its alert threshold: in the warning color with a word, never color alone. */
function Over({ value, over }: { value: number; over: boolean }) {
  return over ? <b className="platform-warn">{count(value)} · alerta</b> : <>{count(value)}</>;
}

/** `routes`: the 5xx, by route (no Retry-After there). */
function ReasonTable({ rows, empty, routes = false }: { rows: PlatformWatch["reasons"]; empty: string; routes?: boolean }) {
  if (rows.length === 0) return <p className="esc-muted">{empty}</p>;
  return (
    <div className="esc-table-wrap">
      <table className="esc-table">
        <thead>
          <tr>
            <th>{routes ? "Ruta" : "Motivo"}</th>
            <th className="esc-num">Veces</th>
            {!routes && <th className="esc-num esc-hide-sm">Con Retry-After</th>}
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.reason + r.detail}>
              <td className="platform-wrap">
                {routes ? r.detail : reasonName(r.reason)}
                {r.reason === "RateLimit" && <span className="esc-table__sub">{policyNames[r.detail] ?? r.detail}</span>}
              </td>
              <td className="esc-num">{count(r.count)}</td>
              {!routes && <td className="esc-num esc-hide-sm">{r.reason.startsWith("Gate.PerIp") || r.reason === "RateLimit" ? count(r.retryAfter) : "—"}</td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function StoreList({ rows, empty, detail, pill }: { rows: WatchStore[]; empty: string; detail: (s: WatchStore) => string; pill: (s: WatchStore) => [string, string] }) {
  if (rows.length === 0) return <p className="esc-muted">{empty}</p>;
  return (
    <ul className="platform-kv">
      {rows.map((s) => {
        const [kind, label] = pill(s);
        return (
          <li key={s.storeId}>
            <span>
              <Link to={`/app/platform/stores/${s.storeId}`}>{s.storeName}</Link>
              <span className="esc-table__sub">
                {s.tenantName} · {detail(s)}
              </span>
            </span>
            <span className={`esc-pill esc-pill--${kind}`}>{label}</span>
          </li>
        );
      })}
    </ul>
  );
}
