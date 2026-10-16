import type { ReactNode } from "react";
import { Link } from "react-router";
import { contactHref, site } from "./config";
import { usePlanPrices } from "./livePrices";

/** TK-245: shared blocks of the public site ("less text, more visuals", docs/diseno/sitio-inicio-maqueta.html). Colors only through site.css tokens. */

/** Internal paths through the router, everything else (WhatsApp, DGII) as a plain link. */
export function To({ href, className, children }: { href: string; className?: string; children: ReactNode }) {
  return href.startsWith("/") ? (
    <Link className={className} to={href}>
      {children}
    </Link>
  ) : (
    <a className={className} href={href} rel="noopener">
      {children}
    </a>
  );
}

export const Tbd = ({ children = "Por confirmar" }: { children?: ReactNode }) => <span className="tbd">{children}</span>;

/** The trial starts with the installer (TK-196): 14 days from the first sale. */
export const Trial = ({ className = "btn" }: { className?: string }) => (
  <Link className={className} to="/descargar">
    Probar 14 días gratis
  </Link>
);

export const Contact = ({ children, className = "btn btn--ghost" }: { children?: string; className?: string }) => (
  <To className={className} href={contactHref}>
    {children ?? (site.whatsapp ? "Hablar por WhatsApp" : "Hablemos")}
  </To>
);

export const Section = ({ id, className, children }: { id?: string; className?: string; children: ReactNode }) => (
  <section id={id} className={className}>
    <div className="wrap">{children}</div>
  </section>
);

export function Hero({ eyebrow, title, sub, cta, note, art, children }: { eyebrow?: string; title: string; sub?: ReactNode; cta?: ReactNode; note?: ReactNode; art?: ReactNode; children?: ReactNode }) {
  return (
    <section className={art ? "hero hero--art" : "hero"}>
      <div className="wrap hero__grid">
        <div className="hero__text">
          {eyebrow && <span className="eyebrow">{eyebrow}</span>}
          <h1>{title}</h1>
          {sub && <p className="hero__sub">{sub}</p>}
          {children}
          {cta && <div className="actions">{cta}</div>}
          {note && <p className="hero__note">{note}</p>}
        </div>
        {art}
      </div>
    </section>
  );
}

const paths = {
  offline: "M2 8.8a15 15 0 0 1 20 0 M5 12.5a10 10 0 0 1 14 0 M8.5 16.1a5 5 0 0 1 7 0 M12 20h.01 M3 3l18 18",
  scale: "M3 21h18 M6 21l2-9h8l2 9 M12 12V7 M8 7h8 M9 3h6",
  ecf: "M6 2h9l5 5v15H6z M14 2v6h6 M9 15l2 2 4-4",
  register: "M4 5h16v14H4z M4 9h16 M8 14h3 M14 14h2",
  tag: "M3 12V4h8l10 10-8 8z M7.5 7.5h.01",
  calendar: "M4 6h16v15H4z M4 10h16 M8 3v5 M16 3v5",
  box: "M3 7l9-4 9 4v10l-9 4-9-4z M3 7l9 4 9-4 M12 11v10",
  stores: "M3 10l2-6h14l2 6 M4 10v10h16V10 M9 20v-5h6v5",
  phone: "M7 2h10v20H7z M11 18h2",
  people: "M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z M2 21c0-4 3-6 7-6s7 2 7 6 M16 3a4 4 0 0 1 0 8 M18 15c2.5.5 4 2.5 4 6",
  qr: "M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h3v3h-3z M20 14v.01 M14 20h.01 M20 20h.01 M17 17h3v3",
  lock: "M5 11h14v10H5z M8 11V7a4 4 0 0 1 8 0v4",
  clock: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z M12 7v5l3 3",
  quote: "M6 2h12v20H6z M9 7h6 M9 11h6 M9 15h3",
  bolt: "M13 2L4 14h7l-1 8 9-12h-7z",
  key: "M15 9a4 4 0 1 0-3.5 4L12 14h2v2h2v2h3v-3l-5.5-5.5 M7.5 7.5h.01",
  shield: "M12 2l8 4v6c0 5-3.5 8.5-8 10-4.5-1.5-8-5-8-10V6z M9 12l2 2 4-4",
  download: "M12 3v12 M7 10l5 5 5-5 M4 21h16",
  pc: "M3 4h18v12H3z M8 21h8 M12 16v5",
  data: "M12 8c5 0 8-1.3 8-3s-3-3-8-3-8 1.3-8 3 3 3 8 3z M4 5v14c0 1.7 3 3 8 3s8-1.3 8-3V5 M4 12c0 1.7 3 3 8 3s8-1.3 8-3",
};
export type IconName = keyof typeof paths;

export const Icon = ({ name, size = 40 }: { name: IconName; size?: number }) => (
  <svg className="icon" aria-hidden="true" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
    <path d={paths[name]} />
  </svg>
);

/** Icon + short title (+ one optional line). */
export function IconTiles({ items, cols = 4 }: { items: [IconName, string, ReactNode?][]; cols?: 2 | 3 | 4 }) {
  return (
    <div className={`tiles tiles--${cols}`}>
      {items.map(([icon, title, text]) => (
        <div className="tile" key={title}>
          <Icon name={icon} />
          <h3>{title}</h3>
          {text && <p>{text}</p>}
        </div>
      ))}
    </div>
  );
}

/** Numbered steps: `round` for the big round numbers, `accentLast` marks the last one as done (the e-CF flow). */
export function Steps({ items, round = false, accentLast = false }: { items: [string, ReactNode][]; round?: boolean; accentLast?: boolean }) {
  return (
    <ol className={round ? "flow flow--round" : "flow"}>
      {items.map(([title, text], i) => (
        <li key={title} className={accentLast && i === items.length - 1 ? "flow__done" : undefined}>
          <h3>{title}</h3>
          <p>{text}</p>
        </li>
      ))}
    </ol>
  );
}

/** Closing band: the entry price from Paddle (TK-249; "Precio por confirmar" while billing is off) and the two calls to action. */
export function CtaBand({ title, text = "Sin cupo de e-CF. Nunca queda en solo lectura.", pricesLink = true }: { title?: ReactNode; text?: ReactNode; pricesLink?: boolean }) {
  const price = usePlanPrices()("Essential"); // the cheapest plan (TK-258)
  return (
    <section className="band">
      <div className="wrap band__grid">
        <div>
          <h2>{title ?? (price ? <>Desde {price}</> : <>Pruebe Abastalia 14 días gratis</>)}</h2>
          <p>{text}</p>
        </div>
        <div className="actions">
          <Trial />
          {pricesLink ? (
            <Link className="btn btn--ghost btn--paper" to="/precios">
              Ver precios
            </Link>
          ) : (
            <Contact className="btn btn--ghost btn--paper" />
          )}
        </div>
      </div>
    </section>
  );
}

/** The e-CF flow on the dark band (home and /e-cf). */
export function EcfFlow({ children }: { children?: ReactNode }) {
  return (
    <section className="night">
      <div className="wrap">
        <h2>e-CF sin intermediarios</h2>
        <p className="night__sub">Su caja firma y envía cada comprobante a la DGII. Sin cupo mensual.</p>
        <Steps
          accentLast
          items={[
            ["Vende", "Cobra como siempre."],
            ["Firma", "Con su certificado digital."],
            ["Envía", "Si no hay internet, espera en cola."],
            ["Aceptado", "Y queda en su 607."],
          ]}
        />
        {children}
      </div>
    </section>
  );
}

const lines: [string, string, string][] = [
  ["Arroz selecto 5 lb", "1 × 245.00", "245.00"],
  ["Queso de freír", "1.250 lb × 220.00", "275.00"],
  ["Aceite 1 L", "2 × 235.00", "470.00"],
  ["Refresco 2 L", "3 × 95.00", "285.00"],
];

/** Hero art: the register screen (sample sale) overlapped by the owner's phone. Decorative: hidden from screen readers, captioned as an example. */
export function ProductArt() {
  return (
    <figure className="art">
      <div className="art__stage" aria-hidden="true">
        <div className="mock">
          <div className="mock__bar">
            <span>Caja 1</span>
            <span className="pill pill--amber">Sin internet · 3 e-CF en cola</span>
          </div>
          <div className="mock__body">
            <div className="mock__cart">
              {lines.map(([name, qty, total], i) => (
                <div className={i === 1 ? "mock__line mock__line--on" : "mock__line"} key={name}>
                  <span>
                    <strong>{name}</strong>
                    <small>{qty}</small>
                  </span>
                  <strong>{total}</strong>
                </div>
              ))}
            </div>
            <div className="mock__side">
              <div className="mock__box">
                <small className="label">Balanza</small>
                <span className="mock__big">
                  1.250 <small>lb</small>
                </span>
                <small className="ok">Estable</small>
              </div>
              <div className="mock__box mock__box--grow">
                <small className="label">Total</small>
                <span className="mock__big">1,275.00</span>
                <small>RD$ · ITBIS incluido</small>
              </div>
              <div className="mock__pay">
                Cobrar <kbd>F12</kbd>
              </div>
            </div>
          </div>
        </div>
        <div className="phone">
          <div className="phone__screen">
            <small className="label">Ventas de hoy</small>
            <span className="mock__big">18,450.00</span>
            <svg className="bars" viewBox="0 0 160 48" width="100%" height="48">
              {[30, 22, 14, 26, 8, 18].map((y, i) => (
                <rect key={i} x={2 + i * 20} y={y} width="14" height={48 - y} rx="2" />
              ))}
              <rect className="bars__today" x="122" y="28" width="14" height="20" rx="2" />
            </svg>
            <span className="phone__row phone__row--top">
              <span>Caja 1</span>
              <span className="ok">Cuadró</span>
            </span>
            <span className="phone__row">
              <span>e-CF</span>
              <span>Todos aceptados</span>
            </span>
          </div>
        </div>
      </div>
      <figcaption>Pantallas de ejemplo; las cifras son ilustrativas.</figcaption>
    </figure>
  );
}

export type Business = "butcher" | "grocery" | "deli";

/** TK-274: Blender renders (design/blender/escenas.py) for each business and the counter. Decorative; width and height are set so nothing shifts while they load. */
export const Render = ({ name, eager = false }: { name: Business | "counter"; eager?: boolean }) => (
  <img className="render" src={`/img/site/${name}.webp`} alt="" width={960} height={600} loading={eager ? "eager" : "lazy"} decoding="async" />
);

/** Hero art for a business page: its render on its tinted panel. */
export const BusinessArt = ({ kind }: { kind: Business }) => (
  <div className={`biz-art biz--${kind}`}>
    <Render name={kind} eager />
  </div>
);

export function BusinessCards() {
  const cards: [Business, string, string, string][] = [
    ["butcher", "/carniceria", "Carnicería", "Balanza, cortes por libra y fiado."],
    ["grocery", "/colmado", "Colmado", "Escáner rápido, fiado y cuadre."],
    ["deli", "/deli", "Deli", "Pedidos, encargos y catálogo con QR."],
  ];
  return (
    <div className="biz">
      {cards.map(([kind, to, name, text]) => (
        <Link className={`biz__card biz--${kind}`} to={to} key={to}>
          <span className="biz__pic">
            <Render name={kind} />
          </span>
          <span className="biz__text">
            <strong>{name}</strong>
            <span>{text}</span>
          </span>
        </Link>
      ))}
    </div>
  );
}

export function Faq({ items }: { items: [string, ReactNode][] }) {
  return (
    <Section className="faq">
      <h2>Preguntas frecuentes</h2>
      {items.map(([q, a]) => (
        <details key={q}>
          <summary>{q}</summary>
          <p>{a}</p>
        </details>
      ))}
    </Section>
  );
}
