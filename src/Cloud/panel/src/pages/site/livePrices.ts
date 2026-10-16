import { useEffect, useState } from "react";
import { apiUrl, priceLabel, type PublicPrices } from "../../api";
import { site } from "./config";

/** The site's price of a plan or add-on code (TK-249, TK-258): the monthly price from Paddle once the page loads, else `config.ts`, else null
 * ("Precio por confirmar"). The pre-rendered HTML always has the fallback; the real price replaces it in the browser. */
export function usePlanPrices(): (code: string) => string | null {
  const [live, setLive] = useState<PublicPrices | null>(null);
  useEffect(() => {
    let alive = true;
    fetch(`${apiUrl}/api/public/prices`, { headers: { Accept: "application/json" } })
      .then((r) => (r.ok ? (r.json() as Promise<PublicPrices>) : null))
      .then((p) => alive && p?.enabled && setLive(p))
      .catch(() => undefined); // offline or billing off: the fallback stays
    return () => {
      alive = false;
    };
  }, []);
  return (code) => {
    const option = [...(live?.plans ?? []), ...(live?.addOns ?? [])].find((p) => p.code === code);
    const price = option?.monthly ?? option?.annual;
    return price ? priceLabel(price) : (site.prices.byCode[code] ?? null);
  };
}
