import { useEffect, useState } from "react";
import { apiUrl, priceLabel, type CatalogPrice, type PublicPrices } from "../../api";
import { site } from "./config";

/** A price as the site shows it (TK-313): `label` "RD$925 al mes" big, `charged` "Se cobra US$15 con tarjeta. Precio final." under it. */
export interface SitePrice {
  label: string;
  charged: string | null;
}

/** The site's prices of plans and add-ons (TK-249, TK-258, TK-293). With Paddle billing on: the monthly price from Paddle, or null ("Precio por
 * confirmar": not sold that way). Until then: the provisional ones in `config.ts` (`provisional` true). The pre-rendered HTML always has the
 * provisional ones; Paddle's replace them in the browser. */
export function usePlanPrices(): { price: (code: string) => SitePrice | null; provisional: boolean } {
  const [live, setLive] = useState<PublicPrices | null>(null);
  useEffect(() => {
    let alive = true;
    fetch(`${apiUrl}/api/public/prices`, { headers: { Accept: "application/json" } })
      .then((r) => (r.ok ? (r.json() as Promise<PublicPrices>) : null))
      .then((p) => alive && p?.enabled && setLive(p))
      .catch(() => undefined); // offline or billing off: the provisional prices stay
    return () => {
      alive = false;
    };
  }, []);
  const price = (code: string) => {
    if (!live) return provisionalPrice(code);
    const option = [...live.plans, ...live.addOns].find((p) => p.code === code);
    const paddle = option?.monthly ?? option?.annual;
    return paddle ? sitePrice(paddle) : null;
  };
  return { price, provisional: !live };
}

/** The price from `config.ts`, written as Paddle's would be. */
export function provisionalPrice(code: string) {
  const usd = site.prices.monthlyUsd[code];
  return usd ? sitePrice({ priceId: "", interval: "Monthly", amount: String(usd * 100), currency: "USD" }) : null;
}

/** US$ → RD$ at `site.prices.dopPerUsd`, rounded to RD$5 (RD$10 from RD$1,000), as in the price list. Other currencies as Paddle has them. */
export function sitePrice(price: CatalogPrice): SitePrice {
  if (price.currency !== "USD") return { label: priceLabel(price), charged: null };
  const usd = Number(price.amount) / 100;
  const dop = usd * site.prices.dopPerUsd;
  const step = dop < 1000 ? 5 : 10;
  const per = price.interval === "Annual" ? " al año" : price.interval === "Monthly" ? " al mes" : "";
  const dollars = usd.toLocaleString("en-US", { minimumFractionDigits: Number.isInteger(usd) ? 0 : 2, maximumFractionDigits: 2 });
  return { label: `RD$${(Math.round(dop / step) * step).toLocaleString("en-US")}${per}`, charged: `Se cobra US$${dollars} con tarjeta. Precio final.` };
}

/** TK-316: e-CF a month per business before we call the owner (fair use in /terminos#uso-justo, docs/cuotas.md). Counted, never charged. */
export const fairUseEcf = 50_000;
/** TK-316: what systems that charge per invoice ask in RD, in RD$ (rounded public rates, October 2026; no brand). Confirm before changing the copy. */
export const perInvoiceDop = { low: 1, high: 2 };
export const maxCalculatorInvoices = 1_000_000;

/** TK-316: the calculator's month as typed: a whole number from 0 to `maxCalculatorInvoices` (empty, negative or not a number = 0). */
export function invoiceCost(typed: string) {
  const n = Math.min(maxCalculatorInvoices, Math.max(0, Math.floor(Number(typed) || 0)));
  return { invoices: n, low: n * perInvoiceDop.low, high: n * perInvoiceDop.high };
}
