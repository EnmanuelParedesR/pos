import type { PaymentMethod } from "./api";

const amountFormat = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** "1,234.50" without the currency (the hero number shows "RD$" smaller). */
export const amount = (value: number) => amountFormat.format(value);

/** "RD$ 1,234.50", the way the owner reads amounts (same as the register and the API's assistant). */
export const money = (value: number) => (value < 0 ? "-" : "") + "RD$ " + amountFormat.format(Math.abs(value));

/** Short amount for chart labels: 950, 12.5k. */
export const shortMoney = (value: number) => (value >= 1000 ? `${(value / 1000).toFixed(1).replace(/\.0$/, "")}k` : String(Math.round(value)));

/** "26.5 lb", "12 unidades", "1 unidad"; just the number when the unit is unknown (sales uploaded before TK-180). */
export const quantity = (value: number, unit?: string | null) => {
  const n = value.toLocaleString("en-US", { maximumFractionDigits: 3 });
  return unit === "Pound" ? `${n} lb` : unit === "Unit" ? `${n} ${value === 1 ? "unidad" : "unidades"}` : n;
};

export const paymentMethodNames: Record<PaymentMethod, string> = {
  Cash: "Efectivo",
  Card: "Tarjeta",
  Transfer: "Transferencia",
  OnAccount: "A crédito",
  CreditBalance: "Saldo a favor",
};

/** Store days are Dominican Republic time (UTC−4, no daylight saving), like the API's BusinessDay. */
const storeOffsetMs = 4 * 60 * 60 * 1000;

export const storeDay = (date = new Date()) => new Date(date.getTime() - storeOffsetMs).toISOString().slice(0, 10);

/** The store's current hour (0–23). */
export const storeHour = (date = new Date()) => new Date(date.getTime() - storeOffsetMs).getUTCHours();

export const addDays = (day: string, days: number) => new Date(Date.parse(day + "T00:00:00Z") + days * 86_400_000).toISOString().slice(0, 10);

const dateTimeFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "America/Santo_Domingo", day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });
const dayFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "UTC", weekday: "short", day: "numeric", month: "short" });

const timeFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "America/Santo_Domingo", hour: "numeric", minute: "2-digit" });
const weekdayFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "UTC", weekday: "long" });

/** A UTC instant's store time: "3:40 p. m.". */
export const time = (utc: string) => timeFormat.format(new Date(utc));

const dayMonthFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "UTC", day: "numeric", month: "long" });

/** "Del 9 al 15 de octubre" or "Del 28 de septiembre al 4 de octubre". */
export function rangeLabel(from: string, to: string) {
  const end = dayMonthFormat.format(new Date(to + "T00:00:00Z"));
  if (from === to) return `El ${end}`;
  const sameMonth = from.slice(0, 7) === to.slice(0, 7);
  return `Del ${sameMonth ? Number(from.slice(8)) : dayMonthFormat.format(new Date(from + "T00:00:00Z"))} al ${end}`;
}

/** A store day's weekday: "jueves". */
export const weekday = (day: string) => weekdayFormat.format(new Date(day + "T00:00:00Z"));

/** A UTC instant in store time: "15 oct, 3:40 p. m.". */
export const dateTime = (utc: string) => dateTimeFormat.format(new Date(utc));

/** A store day (yyyy-MM-dd): "mié, 15 oct". */
export const dayName = (day: string) => dayFormat.format(new Date(day + "T00:00:00Z"));

/** "hace 5 min", "hace 3 h", "hace 2 días". */
export function ago(utc: string, now = Date.now()) {
  const minutes = Math.max(0, Math.floor((now - Date.parse(utc)) / 60_000));
  if (minutes < 1) return "hace un momento";
  if (minutes < 60) return `hace ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `hace ${hours} h`;
  return `hace ${Math.floor(hours / 24)} días`;
}

/** The API flags a store as stale after 24 h without data (PanelQueries.SyncStaleAfter). */
export const isStale = (utc: string | null, now = Date.now()) => utc === null || now - Date.parse(utc) > 24 * 60 * 60 * 1000;

/** The cashier's name (TK-165); until the store sends it, the end of the id, since ids created the same day share their start (version 7). */
export const cashierLabel = (id: string, name?: string | null) => name?.trim() || `Cajero …${id.slice(-4)}`;

/** The customer's name (TK-174); a store on an older version does not send it yet. */
export const customerLabel = (id: string, name?: string | null) => name?.trim() || `Cliente …${id.slice(-4)}`;