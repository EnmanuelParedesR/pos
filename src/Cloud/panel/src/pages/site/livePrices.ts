import { useEffect, useState } from "react";
import { apiUrl, priceLabel, type PublicPrices } from "../../api";
import { site } from "./config";

/** The site's prices of plans and add-ons (TK-249, TK-258, TK-293). With Paddle billing on: the monthly price from Paddle, or null ("Precio por
 * confirmar": not sold that way). Until then: the provisional ones in `config.ts` (`provisional` true). The pre-rendered HTML always has the
 * provisional ones; Paddle's replace them in the browser. */
export function usePlanPrices(): { price: (code: string) => string | null; provisional: boolean } {
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
    return paddle ? priceLabel(paddle) : null;
  };
  return { price, provisional: !live };
}

/** "US$12.00 al mes" from `config.ts`, written as Paddle's would be. */
export function provisionalPrice(code: string) {
  const usd = site.prices.monthlyUsd[code];
  return usd ? priceLabel({ priceId: "", interval: "Monthly", amount: String(usd * 100), currency: "USD" }) : null;
}
