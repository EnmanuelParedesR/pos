import type { ReactNode } from "react";
import type { ApiError, Store } from "./api";
import { addDays, shortMoney, storeDay } from "./format";

/** Logo C5, the tag with the winking eye (same SVG as Escuadra's Logo.razor). */
export function Logo({ size = 28, onFrame = false }: { size?: number; onFrame?: boolean }) {
  return (
    <span className="esc-logo" style={{ fontSize: (size * 7) / 10, color: onFrame ? "var(--frame-text)" : "var(--text)" }}>
      <svg role="img" aria-label="Toki" width={size} height={size} viewBox="0 0 120 120">
        <path d="M44 24 H96 Q104 24 104 32 V88 Q104 96 96 96 H44 L16 60 Z" fill="#0B5CAD" stroke="#0B5CAD" strokeWidth="6" strokeLinejoin="round" />
        <circle cx="35" cy="60" r="8" fill="#FFFFFF" />
        <circle cx="32.5" cy="60" r="3.8" fill="#14181D" />
        <path d="M70 36 V76 Q70 84 78 84 H86 M58 50 H86" fill="none" stroke="#FFFFFF" strokeWidth="10" strokeLinecap="round" strokeLinejoin="round" />
      </svg>
      <span aria-hidden="true">toki</span>
    </span>
  );
}

const icons = {
  alert: "M12 3l10 18H2z M12 10v5 M12 18h.01",
  info: "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 11v6 M12 7h.01",
  check: "M20 6L9 17l-5-5",
};

/** Inline notice, never a modal (Escuadra principle 4). */
export function Notice({ kind, title, children }: { kind: "warning" | "error" | "info" | "success"; title: string; children?: ReactNode }) {
  const icon = kind === "success" ? icons.check : kind === "info" ? icons.info : icons.alert;
  return (
    <div className={`esc-notice esc-notice--${kind}`} role={kind === "error" || kind === "warning" ? "alert" : "status"}>
      <svg aria-hidden="true" width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
        <path d={icon} />
      </svg>
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

/** Plain CSS bar chart, like the register's admin summary. */
export function Bars({ items, label }: { items: { key: string; label: string; value: number; title: string }[]; label: string }) {
  const max = Math.max(0, ...items.map((i) => i.value));
  // Many bars: no value on top and smaller labels (the table below has the numbers).
  const crowded = items.length > 14;
  return (
    <div className={crowded ? "bars bars--crowded" : "bars"} role="img" aria-label={label}>
      {items.map((i) => (
        <div key={i.key} className="bars__bar" title={i.title}>
          {!crowded && <span>{i.value > 0 ? shortMoney(i.value) : ""}</span>}
          <div style={{ height: `${max === 0 ? 0 : (i.value / max) * 100}%` }} />
          <span>{i.label}</span>
        </div>
      ))}
    </div>
  );
}

export function StoreSelect({ stores, value, onChange }: { stores: Store[]; value: string; onChange: (id: string) => void }) {
  if (stores.length < 2) return null;
  return (
    <label className="esc-field">
      <span>Tienda</span>
      <select className="esc-select" value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">Todas las tiendas</option>
        {stores.map((s) => (
          <option key={s.id} value={s.id}>
            {s.name}
          </option>
        ))}
      </select>
    </label>
  );
}

export interface Range { from: string; to: string }

export const lastDays = (days: number): Range => ({ from: addDays(storeDay(), 1 - days), to: storeDay() });

/** 7 days, 30 days or custom dates. */
export function RangePicker({ value, onChange }: { value: Range; onChange: (r: Range) => void }) {
  const preset = [7, 30].find((d) => value.to === storeDay() && value.from === lastDays(d).from);
  return (
    <div className="esc-row">
      {[7, 30].map((d) => (
        <button key={d} type="button" className="esc-chip" aria-pressed={preset === d} onClick={() => onChange(lastDays(d))}>
          {d} días
        </button>
      ))}
      <label className="esc-field">
        <span>Desde</span>
        <input className="esc-input" type="date" value={value.from} max={value.to} onChange={(e) => e.target.value && onChange({ ...value, from: e.target.value })} />
      </label>
      <label className="esc-field">
        <span>Hasta</span>
        <input className="esc-input" type="date" value={value.to} min={value.from} max={storeDay()} onChange={(e) => e.target.value && onChange({ ...value, to: e.target.value })} />
      </label>
    </div>
  );
}

export function Kpi({ label, value, note }: { label: string; value: string; note?: string }) {
  return (
    <div className="esc-card kpi">
      <span className="esc-label">{label}</span>
      <span className="kpi__value">{value}</span>
      {note && <span className="esc-subtitle">{note}</span>}
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="esc-muted empty">{children}</p>;
}
