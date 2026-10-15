import { useState, type ReactNode } from "react";
import type { ApiError, Store } from "./api";
import { addDays, money, shortMoney, storeDay } from "./format";

/** Logo C5, the tag with the winking eye (same SVG as Escuadra's Logo.razor). */
export function Logo({ size = 28, withName = true }: { size?: number; withName?: boolean }) {
  return (
    <span className="esc-logo" style={{ fontSize: (size * 7) / 10, color: "var(--text)" }}>
      <svg role="img" aria-label="Toki" width={size} height={size} viewBox="0 0 120 120">
        <path d="M44 24 H96 Q104 24 104 32 V88 Q104 96 96 96 H44 L16 60 Z" fill="#0B5CAD" stroke="#0B5CAD" strokeWidth="6" strokeLinejoin="round" />
        <circle cx="35" cy="60" r="8" fill="#FFFFFF" />
        <circle cx="32.5" cy="60" r="3.8" fill="#14181D" />
        <path d="M70 36 V76 Q70 84 78 84 H86 M58 50 H86" fill="none" stroke="#FFFFFF" strokeWidth="10" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      {withName && <span aria-hidden="true">toki</span>}
    </span>
  );
}

/** Line icons, Lucide style (2 px stroke): the same set as Escuadra's Icon.razor. Decorative: text carries the meaning. */
const paths = {
  alert: "M12 3l10 18H2z M12 10v5 M12 18h.01",
  info: "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 11v6 M12 7h.01",
  check: "M20 6L9 17l-5-5",
  dashboard: "M4.5 3h4a1.5 1.5 0 0 1 1.5 1.5v6a1.5 1.5 0 0 1-1.5 1.5h-4A1.5 1.5 0 0 1 3 10.5v-6A1.5 1.5 0 0 1 4.5 3z M15.5 3h4A1.5 1.5 0 0 1 21 4.5v2A1.5 1.5 0 0 1 19.5 8h-4A1.5 1.5 0 0 1 14 6.5v-2A1.5 1.5 0 0 1 15.5 3z M15.5 12h4a1.5 1.5 0 0 1 1.5 1.5v6a1.5 1.5 0 0 1-1.5 1.5h-4a1.5 1.5 0 0 1-1.5-1.5v-6a1.5 1.5 0 0 1 1.5-1.5z M4.5 16h4A1.5 1.5 0 0 1 10 17.5v2A1.5 1.5 0 0 1 8.5 21h-4A1.5 1.5 0 0 1 3 19.5v-2A1.5 1.5 0 0 1 4.5 16z",
  chart: "M3 3v18h18 M8 16v-5 M13 16V8 M18 16v-9",
  wallet: "M19 7V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1 M3 5v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4",
  store: "M3 9l1.5-5h15L21 9 M3 9a3 3 0 0 0 6 0 3 3 0 0 0 6 0 3 3 0 0 0 6 0 M5 12v8h14v-8 M10 20v-5h4v5",
  sparkles: "M12 3l1.8 4.7 4.7 1.8-4.7 1.8L12 16l-1.8-4.7-4.7-1.8 4.7-1.8z M19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8z",
  panel: "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 3v18",
  "sign-out": "M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5-5-5 M21 12H9",
  "chevron-right": "M9 6l6 6-6 6",
  "arrow-up": "M12 19V5 M5 12l7-7 7 7",
  "arrow-down": "M12 5v14 M19 12l-7 7-7-7",
  cash: "M4 6h16a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2z M12 9.5a2.5 2.5 0 1 0 0 5a2.5 2.5 0 1 0 0-5z M6 12h.01 M18 12h.01",
  receipt: "M5 3v18l2-1.5L9 21l2-1.5 2 1.5 2-1.5 2 1.5 2-1.5V3l-2 1.5L15 3l-2 1.5L11 3 9 4.5 7 3z M9 9h6 M9 13h6",
  cart: "M2 3h3l2.7 12.4a2 2 0 0 0 2 1.6h8.6a2 2 0 0 0 2-1.6L22 7H6 M9 18.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z M18 18.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3z",
  clock: "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 7v5l3 2",
  cloud: "M17.5 19a4.5 4.5 0 1 0-1.4-8.8A6 6 0 1 0 6 18.9z",
  plus: "M12 5v14 M5 12h14",
  minus: "M5 12h14",
  message: "M21 12a8 8 0 0 1-11.6 7.1L3 21l1.9-6.4A8 8 0 1 1 21 12z",
  send: "M22 2L11 13 M22 2l-7 20-4-9-9-4z",
  inbox: "M22 12h-6l-2 3h-4l-2-3H2 M5.5 5.1L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.5-6.9A2 2 0 0 0 16.8 4H7.2a2 2 0 0 0-1.7 1.1z",
  register: "M5 7h14a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2z M3 12h18 M8 7V4h8v3",
  package: "M21 8l-9-5-9 5v8l9 5 9-5z M3 8l9 5 9-5 M12 13v8",
  calendar: "M5 5h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2z M16 3v4 M8 3v4 M3 10h18",
  shield: "M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z",
  search: "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14z M21 21l-4.35-4.35",
  users: "M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 3a4 4 0 1 0 0 8a4 4 0 1 0 0-8z M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75",
} as const;

export type IconName = keyof typeof paths;

export function Icon({ name, size = 20 }: { name: IconName; size?: number }) {
  return (
    <svg aria-hidden="true" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ flex: "none" }}>
      <path d={paths[name]} />
    </svg>
  );
}

/** Inline notice, never a modal (Escuadra principle 4). */
export function Notice({ kind, title, children }: { kind: "warning" | "error" | "info" | "success"; title: string; children?: ReactNode }) {
  return (
    <div className={`esc-notice esc-notice--${kind}`} role={kind === "error" || kind === "warning" ? "alert" : "status"}>
      <Icon name={kind === "success" ? "check" : kind === "info" ? "info" : "alert"} />
      <div className="esc-notice__body">
        <span>{title}</span>
        {children && <span className="esc-notice__detail">{children}</span>}
      </div>
    </div>
  );
}

/** Shows a failed load: amber when the server is unreachable ("offline is amber, not red"). */
export function LoadError({ error }: { error?: ApiError }) {
  if (!error) return null;
  return <Notice kind={error.offline ? "warning" : "error"} title={error.detail} />;
}

export function PageHead({ title, subtitle, children }: { title: string; subtitle?: ReactNode; children?: ReactNode }) {
  return (
    <div className="esc-page-head">
      <div>
        <h1>{title}</h1>
        {subtitle && <p>{subtitle}</p>}
      </div>
      {children && <div className="esc-page-head__actions">{children}</div>}
    </div>
  );
}

export function Segmented<T extends string>({ label, options, value, onChange }: { label: string; options: [T, string][]; value: T; onChange: (v: T) => void }) {
  return (
    <div className="esc-segmented" role="group" aria-label={label}>
      {options.map(([v, text]) => (
        <button key={v} type="button" aria-pressed={v === value} onClick={() => onChange(v)}>
          {text}
        </button>
      ))}
    </div>
  );
}

/** "+12 %" against a reference. Green up, red down; arrow and sign too (never color alone). */
export function Delta({ current, previous }: { current: number; previous: number }) {
  if (previous <= 0) return null;
  const change = ((current - previous) / previous) * 100;
  const kind = Math.abs(change) < 0.5 ? "flat" : change > 0 ? "up" : "down";
  return (
    <span className={`esc-delta esc-delta--${kind}`}>
      {kind !== "flat" && <Icon name={kind === "up" ? "arrow-up" : "arrow-down"} size={14} />}
      {kind === "flat" ? "Igual" : `${change > 0 ? "+" : "−"}${Math.abs(Math.round(change))} %`}
    </span>
  );
}

/** A small trend line next to a hero number (decorative). */
export function Sparkline({ values, width = 200, height = 64 }: { values: number[]; width?: number; height?: number }) {
  if (values.length < 2) return null;
  const max = Math.max(1, ...values);
  const step = (width - 8) / (values.length - 1);
  const points = values.map((v, i) => [4 + i * step, height - 4 - (v / max) * (height - 8)] as const);
  const line = points.map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  const [lastX, lastY] = points[points.length - 1];
  return (
    <svg className="esc-spark" aria-hidden="true" width={width} height={height} viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none">
      <polygon points={`${points[0][0]},${height} ${line} ${lastX},${height}`} fill="currentColor" opacity="0.1" />
      <polyline points={line} fill="none" stroke="currentColor" strokeWidth="2" strokeLinejoin="round" strokeLinecap="round" vectorEffect="non-scaling-stroke" />
      <circle cx={lastX} cy={lastY} r="3.5" fill="currentColor" />
    </svg>
  );
}

export interface ChartPoint { key: string; label: string; title: string; value: number; ghost?: number; detail?: string; current?: boolean }

/** A round top for the scale (1, 1.5, 2, 2.5, 3, 4, 5, 6, 8 × 10^n) so the bars fill the plot. */
const niceMax = (value: number) => {
  if (value <= 0) return 1;
  const magnitude = 10 ** Math.floor(Math.log10(value));
  return ([1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10].find((f) => value <= f * magnitude) ?? 10) * magnitude;
};

/** Bars with the previous period as a gray ghost beside each bar; hover or focus a column for its tooltip (same as BarChart.razor).
 * `format` writes the values: money by default, counts in the Plataforma area. */
export function BarChart({
  points,
  label,
  ghostLabel = "Semana anterior",
  empty,
  emptyTitle = "Todavía no hay ventas",
  format = money,
}: {
  points: ChartPoint[];
  label: string;
  ghostLabel?: string;
  empty: string;
  emptyTitle?: string;
  format?: (value: number) => string;
}) {
  if (points.every((p) => p.value === 0 && !p.ghost)) return <EmptyState icon="chart" title={emptyTitle}>{empty}</EmptyState>;
  const max = niceMax(Math.max(...points.map((p) => Math.max(p.value, p.ghost ?? 0))));
  const pct = (v: number) => `${Math.min(100, (v / max) * 100)}%`;
  const crowded = points.length > 16;
  return (
    <div className="esc-chart">
      <div className="esc-chart__plot" role="list" aria-label={label} style={crowded ? { gap: 2 } : undefined}>
        <div className="esc-chart__grid" aria-hidden="true">
          {[1, 0.5, 0].map((s) => (
            <div key={s} style={{ top: `${100 - s * 100}%` }}>
              <span>{shortMoney(max * s)}</span>
            </div>
          ))}
        </div>
        {points.map((p) => (
          <div
            key={p.key}
            className={`esc-chart__col${p.current ? " esc-chart__col--current" : ""}`}
            role="listitem"
            tabIndex={0}
            aria-label={`${p.title}: ${format(p.value)}${p.ghost !== undefined ? `, ${ghostLabel}: ${format(p.ghost)}` : ""}`}
          >
            {p.ghost !== undefined && <div className="esc-chart__ghost" style={{ height: pct(p.ghost) }} />}
            <div className="esc-chart__bar" style={{ height: pct(p.value) }} />
            <div className="esc-chart__tip" aria-hidden="true">
              {p.title}
              <b>{format(p.value)}</b>
              {p.detail && (
                <span>
                  {p.detail}
                  <br />
                </span>
              )}
              {p.ghost !== undefined && (
                <span>
                  {ghostLabel}: {format(p.ghost)}
                </span>
              )}
            </div>
          </div>
        ))}
      </div>
      <div className="esc-chart__x" aria-hidden="true" style={crowded ? { gap: 2 } : undefined}>
        {points.map((p, i) => (
          <span key={p.key} className={p.current ? "is-current" : undefined}>
            {crowded && i % 3 !== 0 && i !== points.length - 1 ? "" : p.label}
          </span>
        ))}
      </div>
    </div>
  );
}

export function Legend({ current, ghost }: { current: string; ghost?: string }) {
  return (
    <div className="esc-legend">
      <span>
        <i />
        {current}
      </span>
      {ghost && (
        <span>
          <i className="ghost" />
          {ghost}
        </span>
      )}
    </div>
  );
}

export function EmptyState({ icon = "inbox", title, children }: { icon?: IconName; title: string; children?: ReactNode }) {
  return (
    <div className="esc-empty" role="status">
      <div className="esc-empty__icon">
        <Icon name={icon} size={22} />
      </div>
      <div className="esc-empty__title">{title}</div>
      {children && <p>{children}</p>}
    </div>
  );
}

export function Skeleton({ width = "100%", height = 16 }: { width?: number | string; height?: number | string }) {
  return <span className="esc-skeleton" aria-hidden="true" style={{ width, height }} />;
}

/** Placeholder rows while a table loads. */
export function SkeletonRows({ columns, rows = 5 }: { columns: number; rows?: number }) {
  return (
    <>
      {Array.from({ length: rows }, (_, r) => (
        <tr key={r}>
          {Array.from({ length: columns }, (_, c) => (
            <td key={c}>
              <Skeleton width={c === 0 ? 140 : "70%"} />
            </td>
          ))}
        </tr>
      ))}
    </>
  );
}

export function Card({ title, aside, children, className = "" }: { title?: string; aside?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`esc-card esc-card--padded ${className}`} aria-label={title}>
      {title && (
        <div className="esc-card__head">
          <h2 className="esc-card__title">{title}</h2>
          {aside && <span className="esc-card__aside">{aside}</span>}
        </div>
      )}
      {children}
    </section>
  );
}

export function StoreSelect({ stores, value, onChange }: { stores: Store[]; value: string; onChange: (id: string) => void }) {
  if (stores.length < 2) return null;
  return (
    <select className="esc-select" style={{ width: "auto" }} aria-label="Tienda" value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">Todas las tiendas</option>
      {stores.map((s) => (
        <option key={s.id} value={s.id}>
          {s.name}
        </option>
      ))}
    </select>
  );
}

export interface Range { from: string; to: string }

export const lastDays = (days: number, to = storeDay()): Range => ({ from: addDays(to, 1 - days), to });

/** The same number of days right before a range (for the comparison). */
export const previousRange = (r: Range): Range => {
  const days = Math.round((Date.parse(r.to) - Date.parse(r.from)) / 86_400_000) + 1;
  return { from: addDays(r.from, -days), to: addDays(r.from, -1) };
};

/** 7 days, 30 days, this month or custom dates (shown only when chosen). */
export function RangePicker({ value, onChange }: { value: Range; onChange: (r: Range) => void }) {
  const today = storeDay();
  const month = { from: today.slice(0, 8) + "01", to: today };
  const preset = value.to !== today ? "custom" : value.from === lastDays(7).from ? "7" : value.from === lastDays(30).from ? "30" : value.from === month.from ? "month" : "custom";
  const [custom, setCustom] = useState(preset === "custom");
  const choose = (v: string) => {
    setCustom(v === "custom");
    if (v === "7") onChange(lastDays(7));
    if (v === "30") onChange(lastDays(30));
    if (v === "month") onChange(month);
  };
  return (
    <>
      <Segmented label="Periodo" options={[["7", "7 días"], ["30", "30 días"], ["month", "Este mes"], ["custom", "Fechas"]]} value={custom ? "custom" : preset} onChange={choose} />
      {custom && (
        <div className="esc-row" style={{ flexWrap: "nowrap" }}>
          <input className="esc-input" type="date" aria-label="Desde" value={value.from} max={value.to} onChange={(e) => e.target.value && onChange({ ...value, from: e.target.value })} />
          <input className="esc-input" type="date" aria-label="Hasta" value={value.to} min={value.from} max={today} onChange={(e) => e.target.value && onChange({ ...value, to: e.target.value })} />
        </div>
      )}
    </>
  );
}

export function Stat({ icon, label, value, note }: { icon: IconName; label: string; value: string; note?: string }) {
  return (
    <div className="esc-stat">
      <span className="esc-stat__label">
        <Icon name={icon} size={16} /> {label}
      </span>
      <span className="esc-stat__value">{value}</span>
      {note && <span className="esc-stat__note">{note}</span>}
    </div>
  );
}

/** Proportion bars: name, optional detail, amount (or `format`) and a bar relative to the largest. */
export function BarList({ items, format = money }: { items: { key: string; name: string; detail?: string; value: number; share: number }[]; format?: (value: number) => string }) {
  return (
    <ul className="esc-barlist">
      {items.map((i) => (
        <li key={i.key}>
          <div className="esc-barlist__row">
            <span className="esc-barlist__name">{i.name}</span>
            {i.detail && <span className="esc-barlist__qty">{i.detail}</span>}
            <span className="esc-barlist__value">{format(i.value)}</span>
          </div>
          <div className="esc-barlist__track">
            <div style={{ width: `${Math.max(0, Math.min(100, i.share * 100))}%` }} />
          </div>
        </li>
      ))}
    </ul>
  );
}
