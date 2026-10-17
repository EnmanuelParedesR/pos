import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router";
import { Talia } from "../../ui";
import { contactHref, site } from "./config";
import { usePlanPrices } from "./livePrices";
import productVideo from "./productVideo.json";

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
  meat: "M12.5 6a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5z M12.5 2a6.5 6.5 0 0 0-6.2 4.6c-1.1 3.1-.8 3.9-3.2 6.1A3 3 0 0 0 5 18c4 0 8.4-1.8 11.4-4.3A6.5 6.5 0 0 0 12.5 2z M18.5 6l2.2 4.5a6.5 6.5 0 0 1-2.3 7.2C15.4 20.2 11 22 7 22a3 3 0 0 1-2.7-1.7L2.4 16.5",
  basket: "M3 10h18l-2 10H5z M8 10l4-6 4 6 M9 14v3 M12 14v3 M15 14v3",
  cheese: "M3 19V11l13-7 5 5v10z M3 11h18 M8 15h.01 M15 14h.01 M12 17h.01",
  chat: "M21 12a8 8 0 0 1-11.6 7.1L4 21l1.9-5.4A8 8 0 1 1 21 12z",
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

/** Closing band: the entry price (TK-249; TK-293: the provisional one while billing is off) and the two calls to action. */
export function CtaBand({ title, text = "Sin cupo de e-CF. Sus datos y reportes nunca se bloquean.", pricesLink = true }: { title?: ReactNode; text?: ReactNode; pricesLink?: boolean }) {
  const prices = usePlanPrices();
  const price = prices.price("Essential"); // the cheapest plan (TK-258)
  return (
    <section className="band">
      <div className="wrap band__grid">
        <div>
          <h2>{title ?? (price ? <>Desde {price.label}</> : <>Pruebe Abastalia 14 días gratis</>)}</h2>
          <p>{text}</p>
          {!title && price?.charged && <p className="band__note">{price.charged}{prices.provisional && " Precios provisionales."}</p>}
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

/** TK-312: the e-CF's real path on the dark band (home and /e-cf): the register, the signature with the business's certificate on its own PC, the
 *  DGII directly and its answer; below, what happens without internet and what is not in the middle. The chips are the register's own text
 *  (Frame.razor: "e-CF en cola: N"). With motion allowed, the steps light up in turn the first time the band comes on screen (.reveal). */
export function EcfFlow({ children }: { children?: ReactNode }) {
  return (
    <section className="night ecf reveal">
      <div className="wrap">
        <h2>e-CF sin intermediarios</h2>
        <p className="night__sub">Su caja firma cada comprobante y lo envía directo a la DGII.</p>
        <ol className="ecf__path">
          <li className="ecf__node">
            <span className="ecf__k">En su tienda</span>
            <h3>Vende</h3>
            <p>Cobra como siempre.</p>
            <span className="ecf__chip">e-CF en cola: 0</span>
          </li>
          <li className="ecf__node">
            <span className="ecf__k">En su PC</span>
            <h3>Firma</h3>
            <p>Con su certificado digital, que no sale de la PC principal.</p>
          </li>
          <li className="ecf__node">
            <span className="ecf__k">Directo</span>
            <h3>Envía a la DGII</h3>
            <p>Sin un proveedor en medio.</p>
          </li>
          <li className="ecf__node ecf__node--ok">
            <span className="ecf__k">Respuesta</span>
            <h3>Aceptado</h3>
            <p>
              <span className="ecf__mono">E320000000123</span> y queda en su 607.
            </p>
          </li>
        </ol>
        <div className="ecf__offline">
          <span className="ecf__chip ecf__chip--warn">e-CF en cola: 3</span>
          <p>Si se va el internet, la caja sigue cobrando y firmando. Los e-CF esperan en cola y salen solos al volver la conexión, dentro de las 72 horas que da la DGII.</p>
        </div>
        <ul className="ecf__none" aria-label="Lo que no hay en medio">
          <li>Ningún proveedor externo</li>
          <li>Ningún cupo de documentos al mes</li>
          <li>Ningún cobro por comprobante</li>
        </ul>
        {children}
      </div>
    </section>
  );
}

/** TK-312: what Talia answers today with her read-only tools (Assistant.cs). The first answer is the one the product video captured from the panel
 *  (TK-296); the rest are examples with sample data. The last item is the screen her «Ir a» button opens (TK-339, assistantRoutes.json). */
const taliaAsks: [string, string, string][] = [
  ["¿Qué se vendió más esta semana?", "Esta semana lo más vendido fue Huevos (30): RD$ 19,470.00. Le siguen Arroz selecto 5 lb (RD$ 16,530.00) y Café molido 1 lb (RD$ 15,300.00).", "Ventas"],
  ["¿Qué cajera tuvo faltante esta semana?", "María tuvo un faltante de RD$ 150.00 al cerrar el turno del martes en la Caja 1. Los demás turnos cuadraron.", "Cuadres"],
  ["¿Cuánto me deben y quién debe más?", "Hoy le deben RD$ 12,840.00 entre 9 clientes. Quien más debe es Pedro Gómez, con RD$ 3,200.00.", "Resumen"],
  ["¿En qué paso va mi certificación?", "Va en el set de pruebas: Abastalia ya envió los comprobantes y espera la respuesta de la DGII. Después sigue la simulación.", "Certificación"],
];

/** TK-312: hero art of the home. Talia as the panel shows her (same bubbles), all HTML: sharp at any size and no image to wait for (LCP). A question
 *  button shows its answer here; nothing is sent anywhere. With motion allowed, the question and then the answer ease in. */
export function TaliaDemo() {
  const [at, setAt] = useState(0);
  const [question, answer, go] = taliaAsks[at];
  return (
    <figure className="talia-demo">
      <div className="talia-demo__phone">
        <div className="talia-demo__head">
          <Talia size={36} />
          <span>
            <strong>Talia</strong>
            <small>Su asistente</small>
          </span>
        </div>
        <div className="talia-demo__chat" key={at} aria-live="polite">
          <p className="talia-demo__q">{question}</p>
          <div className="talia-demo__row">
            <Talia size={28} />
            <p className="talia-demo__a">{answer}</p>
          </div>
          <span className="talia-demo__go">Ir a {go} →</span>
        </div>
      </div>
      <div className="talia-demo__asks" role="group" aria-label="Pregúntele a Talia">
        {taliaAsks.map(([q], i) => (
          <button type="button" key={q} aria-pressed={i === at} onClick={() => setAt(i)}>
            {q}
          </button>
        ))}
      </div>
      <figcaption>Así responde Talia en el panel. Datos de ejemplo.</figcaption>
    </figure>
  );
}

export type Business = "butcher" | "grocery" | "deli";

const photoAlt: Record<Business, string> = {
  butcher: "Clienta comprando en el mostrador de una carnicería, con embutidos colgados",
  grocery: "Estantes de una tienda de barrio llenos de productos",
  deli: "Mostrador de un deli con jamones, quesos y balanza",
};
const photoName: Record<Business, string> = { butcher: "carniceria", grocery: "colmado", deli: "deli" };
const widths = [480, 800, 1200];

/** TK-281: a real photo or screenshot from scripts/images.mjs (AVIF and WebP at three widths). Width and height are set so nothing shifts while it loads. */
export function Photo({ name, alt, ratio, sizes, eager = false }: { name: string; alt: string; ratio: number; sizes: string; eager?: boolean }) {
  const set = (ext: string) => widths.map((w) => `/img/site/${name}-${w}.${ext} ${w}w`).join(", ");
  return (
    <picture className="photo">
      <source type="image/avif" srcSet={set("avif")} sizes={sizes} />
      <img src={`/img/site/${name}-800.webp`} srcSet={set("webp")} sizes={sizes} alt={alt} width={1200} height={Math.round(1200 / ratio)} loading={eager ? "eager" : "lazy"} decoding="async" />
    </picture>
  );
}

/** "0:03" for a number of seconds. */
const clock = (s: number) => `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;

/** TK-296: the product video (design/video/README.md). Never plays by itself: a big button over the poster starts it, then the browser's
 *  controls. Like LoopVideo, the poster is set only near the screen (LCP), and width/height are fixed (no shift). The text on screen and the
 *  visible transcript come from the same productVideo.json as the burned-in captions and the descriptions track. */
export function ProductVideo() {
  const ref = useRef<HTMLVideoElement>(null);
  const [near, setNear] = useState(false);
  const [started, setStarted] = useState(false);
  const transcript = useId();
  const media = `/media/${productVideo.name}`;
  useEffect(() => {
    const video = ref.current;
    if (!video) return;
    if (typeof IntersectionObserver === "undefined") return setNear(true);
    const soon = new IntersectionObserver(([e]) => e.isIntersecting && setNear(true), { rootMargin: "300px" });
    soon.observe(video);
    return () => soon.disconnect();
  }, []);
  const play = () => {
    setStarted(true);
    ref.current?.focus(); // the button goes away: keep the keyboard on the video (its controls)
    ref.current?.play().catch(() => {});
  };
  let at = 0;
  const lines = productVideo.shots.map((s) => {
    const line = { time: clock(at), text: s.title ? `${s.title}: ${s.caption}` : s.caption, description: s.description };
    at += s.seconds;
    return line;
  });
  return (
    <figure className="product-video">
      <div className="product-video__frame">
        <video ref={ref} className="product-video__video" controls={started} muted playsInline preload="none" poster={near ? `${media}-portada.webp` : undefined}
          width={productVideo.width} height={productVideo.height} aria-label={productVideo.label} aria-describedby={transcript}>
          <source src={`${media}.webm`} type="video/webm" />
          <source src={`${media}.mp4`} type="video/mp4" />
          <track kind="descriptions" srcLang="es" label="Español" src={`${media}.es.vtt`} default />
        </video>
        {!started && (
          <button type="button" className="product-video__play" onClick={play}>
            <span className="product-video__icon" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="28" height="28"><path d="M8 5v14l11-7z" fill="currentColor" /></svg>
            </span>
            <span className="product-video__label">Ver el video ({clock(at)})</span>
          </button>
        )}
      </div>
      <details className="product-video__transcript" id={transcript}>
        <summary>Ver lo que muestra el video</summary>
        <ol>
          {lines.map((l) => (
            <li key={l.time}>
              <span className="product-video__time">{l.time}</span> <strong>{l.text}.</strong> {l.description}
            </li>
          ))}
        </ol>
      </details>
    </figure>
  );
}

/** Hero art for a business page: a real photo of that kind of counter. */
export const BusinessArt = ({ kind }: { kind: Business }) => (
  <div className={`biz-art biz--${kind}`}>
    <Photo name={photoName[kind]} alt={photoAlt[kind]} ratio={4 / 3} sizes="(min-width: 900px) 480px, 100vw" eager />
  </div>
);

/** TK-281: the businesses on the home as icons (quick to scan, light on a phone); the photos live on each business page. */
export function BusinessCards() {
  const cards: [Business, IconName, string, string, string][] = [
    ["butcher", "meat", "/carniceria", "Carnicería", "Etiquetas de balanza, encargos y cortes."],
    ["grocery", "basket", "/colmado", "Colmado", "Escáner rápido, crédito y cuadre."],
    ["deli", "cheese", "/deli", "Deli", "Pedidos, encargos y catálogo con QR."],
  ];
  return (
    <div className="biz">
      {cards.map(([kind, icon, to, name, text]) => (
        <Link className={`biz__card biz--${kind}`} to={to} key={to}>
          <span className="biz__icon">
            <Icon name={icon} />
          </span>
          <span className="biz__text">
            <strong>{name}</strong>
            <span>{text}</span>
          </span>
          <span className="biz__go" aria-hidden="true">→</span>
        </Link>
      ))}
    </div>
  );
}

/** TK-281: how it works, each step with a real screenshot of the register, what it takes and how long. A vertical timeline on a phone, three columns on a wide screen. */
/** TK-296: without `shot` (the home, where the video shows the product) the steps are text only. */
export function HowItWorks({ items }: { items: { title: string; text: ReactNode; time: string; shot?: string; alt?: string }[] }) {
  return (
    <ol className="how">
      {items.map((s, i) => (
        <li key={s.title} className="how__step">
          <span className="how__num" aria-hidden="true">{i + 1}</span>
          {s.shot && (
            <div className="how__shot">
              <Photo name={s.shot} alt={s.alt ?? ""} ratio={16 / 10} sizes="(min-width: 900px) 360px, 100vw" />
            </div>
          )}
          <div className="how__text">
            <h3>{s.title}</h3>
            <p>{s.text}</p>
            <span className="how__time"><Icon name="clock" /> {s.time}</span>
          </div>
        </li>
      ))}
    </ol>
  );
}
/** TK-294: cards in a row that scrolls sideways (native scroll-snap; swipe, the arrow keys on the focused row, or the buttons, which move one
 * card). Without JavaScript it still scrolls. Smooth only when the device does not ask for reduced motion. */
export function Carousel({ title, hint, slides }: { title: string; hint: ReactNode; slides: { icon: IconName; title: string; body: ReactNode }[] }) {
  const id = useId();
  const track = useRef<HTMLDivElement>(null);
  const [edge, setEdge] = useState({ start: true, end: false });
  useEffect(() => {
    const el = track.current!;
    const update = () => setEdge({ start: el.scrollLeft <= 1, end: el.scrollLeft + el.clientWidth >= el.scrollWidth - 1 });
    update();
    el.addEventListener("scroll", update, { passive: true });
    window.addEventListener("resize", update);
    return () => {
      el.removeEventListener("scroll", update);
      window.removeEventListener("resize", update);
    };
  }, []);
  const go = (direction: 1 | -1) => {
    const el = track.current!;
    const card = el.firstElementChild as HTMLElement;
    const step = card.offsetWidth + parseFloat(getComputedStyle(el).columnGap || "0");
    el.scrollBy({ left: direction * step, behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth" });
  };
  return (
    <section className="counter carousel" aria-roledescription="carrusel" aria-labelledby={`${id}t`}>
      <div className="wrap">
        <div className="carousel__head">
          <div>
            <h2 id={`${id}t`}>{title}</h2>
            <p className="muted">{hint}</p>
          </div>
          <div className="carousel__nav">
            <button type="button" className="carousel__btn" aria-controls={`${id}r`} aria-label="Anterior" disabled={edge.start} onClick={() => go(-1)}>
              <svg aria-hidden="true" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M15 6l-6 6 6 6" /></svg>
            </button>
            <button type="button" className="carousel__btn" aria-controls={`${id}r`} aria-label="Siguiente" disabled={edge.end} onClick={() => go(1)}>
              <svg aria-hidden="true" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M9 6l6 6-6 6" /></svg>
            </button>
          </div>
        </div>
        <div className="carousel__track" id={`${id}r`} ref={track} tabIndex={0} role="group" aria-label={`${title}: ${slides.length} tarjetas`}>
          {slides.map((s, i) => (
            <div className="slide" key={s.title} role="group" aria-roledescription="diapositiva" aria-label={`${i + 1} de ${slides.length}: ${s.title}`}>
              <div className="slide__head">
                <Icon name={s.icon} size={32} />
                <h3>{s.title}</h3>
              </div>
              {s.body}
            </div>
          ))}
        </div>
      </div>
    </section>
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
