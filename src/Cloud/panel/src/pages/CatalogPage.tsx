import { useState } from "react";
import { useParams } from "react-router";
import { apiUrl, useApi, type PublicCatalog, type PublicCatalogItem } from "../api";
import { money, quantity } from "../format";
import { Icon, LoadError, Logo, Notice, Skeleton } from "../ui";

/** By the pound in half pounds, by the unit one at a time. */
const step = (item: PublicCatalogItem) => (item.unit === "Pound" ? 0.5 : 1);
const unitLabel = (item: PublicCatalogItem) => (item.unit === "Pound" ? "lb" : "und.");

/** The WhatsApp text: what the customer picked, with the prices they saw and an approximate total (weights change at the counter). */
export function orderMessage(storeName: string, items: PublicCatalogItem[], quantities: Record<string, number>) {
  const chosen = items.filter((i) => (quantities[i.productId] ?? 0) > 0);
  const lines = chosen.map((i) => `• ${quantity(quantities[i.productId])} ${unitLabel(i)} ${i.name} (${money(i.price)}/${unitLabel(i)})`);
  const total = chosen.reduce((sum, i) => sum + Math.round(i.price * quantities[i.productId] * 100) / 100, 0);
  return [`Hola, ${storeName}. Quiero pedir:`, ...lines, `Total aproximado: ${money(total)}`].join("\n");
}

export const whatsAppLink = (phone: string, text: string) => `https://wa.me/${phone.replace(/\D/g, "")}?text=${encodeURIComponent(text)}`;

/** TK-195: a store's public catalog, opened from its QR or a shared link. No sign-in, no cart or payment: the order goes by WhatsApp. */
export function CatalogPage() {
  const { storeId = "" } = useParams();
  const catalog = useApi<PublicCatalog>(`/api/public/catalog/${encodeURIComponent(storeId)}`);
  const [quantities, setQuantities] = useState<Record<string, number>>({});
  const items = catalog.data?.items ?? [];
  const categories = [...new Set(items.map((i) => i.category))];
  const count = items.filter((i) => (quantities[i.productId] ?? 0) > 0).length;
  const change = (item: PublicCatalogItem, sign: 1 | -1) =>
    setQuantities((q) => ({ ...q, [item.productId]: Math.max(0, (q[item.productId] ?? 0) + sign * step(item)) }));

  return (
    <div className="esc-admin catalog">
      <header className="catalog__head">
        {catalog.data?.logoVersion && (
          // TK-280: the store's own logo (a PNG its server wrote); the name below says whose it is, so it is decorative.
          <img
            className="catalog__logo"
            src={`${apiUrl}/api/public/catalog/${encodeURIComponent(storeId)}/logo?v=${encodeURIComponent(catalog.data.logoVersion)}`}
            alt=""
          />
        )}
        <h1>{catalog.data?.storeName ?? "Catálogo"}</h1>
        <p>Elija lo que quiere y envíe su pedido por WhatsApp. Los productos por libra se cobran con el peso real.</p>
      </header>
      <main className="catalog__list">
        {catalog.loading && <Skeleton height={56} />}
        <LoadError error={catalog.error} />
        {catalog.data && items.length === 0 && <Notice kind="info" title="Este catálogo todavía no tiene productos." />}
        {categories.map((category, index) => (
          <section key={category} aria-labelledby={`c-${index}`}>
            <h2 id={`c-${index}`} className="catalog__category">
              {category}
            </h2>
            <ul>
              {items
                .filter((i) => i.category === category)
                .map((i) => {
                  const q = quantities[i.productId] ?? 0;
                  return (
                    <li key={i.productId} className="catalog__item">
                      <div>
                        <b>{i.name}</b>
                        <span>
                          {money(i.price)} / {unitLabel(i)}
                        </span>
                      </div>
                      <div className="catalog__stepper" role="group" aria-label={`Cantidad de ${i.name}`}>
                        <button type="button" className="esc-button esc-button--small" aria-label={`Menos ${i.name}`} disabled={q === 0} onClick={() => change(i, -1)}>
                          <Icon name="minus" size={18} />
                        </button>
                        <output aria-live="polite">{q === 0 ? "0" : `${quantity(q)} ${unitLabel(i)}`}</output>
                        <button type="button" className="esc-button esc-button--small" aria-label={`Más ${i.name}`} onClick={() => change(i, 1)}>
                          <Icon name="plus" size={18} />
                        </button>
                      </div>
                    </li>
                  );
                })}
            </ul>
          </section>
        ))}
      </main>
      {catalog.data && (
        <footer className="catalog__bar">
          {catalog.data.whatsApp ? (
            count > 0 ? (
              <a className="esc-button esc-button--primary catalog__send" href={whatsAppLink(catalog.data.whatsApp, orderMessage(catalog.data.storeName, items, quantities))} target="_blank" rel="noopener">
                <Icon name="message" /> Pedir por WhatsApp ({count})
              </a>
            ) : (
              <span className="esc-muted">Toque + en lo que quiere pedir.</span>
            )
          ) : (
            <span className="esc-muted">Esta tienda todavía no recibe pedidos por WhatsApp.</span>
          )}
          <Logo size={20} />
        </footer>
      )}
    </div>
  );
}
