import { useState, type ReactNode } from "react";
import { Link, useLocation, useNavigate, useParams } from "react-router";
import {
  api,
  roleNames,
  useApi,
  type CertificationProgress,
  type DailyActivity,
  type FunnelStage,
  type MetricsOverview,
  type PlatformNode,
  type PlatformStoreDetail,
  type PlatformTenant,
  type PlatformTenantDetail,
  type RetentionCohort,
} from "../api";
import { ago, dateTime, dayName, isStale, money } from "../format";
import { BarChart, BarList, Card, ConfirmButton, EmptyState, Icon, LoadError, Notice, PageHead, Skeleton, SkeletonRows, Stat } from "../ui";

/** Plataforma (TK-189 / TK-190): Toki staff only. Same Escuadra Admin pieces as the owner's panel. */

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
  ecfRelay: "e-CF por la nube",
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
  const names: Record<string, string> = { Active: "Activa", Grace: "En gracia", Restricted: "Restringida" };
  return status === "Restricted" || !endsUtc ? (names[status] ?? status) : `${names[status] ?? status} · vence ${dayName(endsUtc.slice(0, 10))}`;
}

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
  ];
  const current = pathname === "/app/platform" ? "/app/platform" : "/app/platform/tenants";
  return (
    <div className="esc-tabs" role="tablist" aria-label="Plataforma">
      {tabs.map(([to, label]) => (
        <button key={to} type="button" role="tab" aria-selected={to === current} onClick={() => navigate(to)}>
          {label}
        </button>
      ))}
    </div>
  );
}

const staffNote = "Solo para el equipo de Toki. Cada consulta queda registrada.";

export function PlatformMetricsPage() {
  const overview = useApi<MetricsOverview>("/api/platform/metrics/overview");
  const daily = useApi<DailyActivity[]>("/api/platform/metrics/daily?days=30");
  const retention = useApi<RetentionCohort[]>("/api/platform/metrics/retention?weeks=8");
  const o = overview.data;

  return (
    <>
      <PageHead title="Plataforma" subtitle={staffNote} />
      <PlatformTabs />
      <LoadError error={overview.error} />
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
  const { data, error, loading } = useApi<PlatformTenant[]>(`/api/platform/tenants${query ? `?search=${encodeURIComponent(query)}` : ""}`);
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
      <LoadError error={error} />
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
      <LoadError error={error} />
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
      <LoadError error={error} />
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
              Facturación electrónica: {c.fiscalProvider === "Relay" ? "por la nube (DGII)" : c.fiscalProvider === "Simulated" ? "simulada" : "sin datos"}
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
