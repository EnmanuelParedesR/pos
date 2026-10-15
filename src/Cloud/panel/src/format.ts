import type { PaymentMethod } from "./api";

const amount = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** "RD$ 1,234.50", the way the owner reads amounts (same as the register and the API's assistant). */
export const money = (value: number) => (value < 0 ? "-" : "") + "RD$ " + amount.format(Math.abs(value));

/** Short amount for chart labels: 950, 12.5k. */
export const shortMoney = (value: number) => (value >= 1000 ? `${(value / 1000).toFixed(1).replace(/\.0$/, "")}k` : String(Math.round(value)));

export const quantity = (value: number) => value.toLocaleString("en-US", { maximumFractionDigits: 3 });

export const paymentMethodNames: Record<PaymentMethod, string> = {
  Cash: "Efectivo",
  Card: "Tarjeta",
  Transfer: "Transferencia",
  OnAccount: "Fiado",
  CreditBalance: "Saldo a favor",
};

/** Store days are Dominican Republic time (UTC−4, no daylight saving), like the API's BusinessDay. */
const storeOffsetMs = 4 * 60 * 60 * 1000;

export const storeDay = (date = new Date()) => new Date(date.getTime() - storeOffsetMs).toISOString().slice(0, 10);

export const addDays = (day: string, days: number) => new Date(Date.parse(day + "T00:00:00Z") + days * 86_400_000).toISOString().slice(0, 10);

const dateTimeFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "America/Santo_Domingo", day: "numeric", month: "short", hour: "numeric", minute: "2-digit" });
const dayFormat = new Intl.DateTimeFormat("es-DO", { timeZone: "UTC", weekday: "short", day: "numeric", month: "short" });

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

/** Cashiers only have an id: show its end, since ids created the same day share their start (version 7). */
// shortcut: "Cajero …1a2b" until the store sends a user event with names (pending in TK-133); then show the name.
export const cashierLabel = (id: string) => `Cajero …${id.slice(-4)}`;
