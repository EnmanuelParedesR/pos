import { useEffect } from "react";
import { Link, NavLink, Outlet, useLocation } from "react-router";
import { Logo, ThemeToggle } from "../../ui";
import { site } from "./config";
import { Trial } from "./SiteParts";
import { SiteTalia } from "./SiteTalia";
import { notFound, sitePages, type SitePage } from "./SitePages";

export const pageFor = (pathname: string): SitePage => sitePages.find((p) => p.path === pathname) ?? notFound;

/** <title>, description and canonical for the page on screen; the prerender writes the same into each page's HTML for crawlers. */
function useHead(page: SitePage, pathname: string) {
  useEffect(() => {
    document.title = page.title;
    const set = (selector: string, make: () => HTMLElement, attr: string, value: string) => {
      const el = document.head.querySelector(selector) ?? document.head.appendChild(make());
      el.setAttribute(attr, value);
    };
    set('meta[name="description"]', () => Object.assign(document.createElement("meta"), { name: "description" }), "content", page.description);
    // R12 #12: a client-side 404 drops the canonical the previous page left (a 404 must never claim an address).
    if (page !== notFound) set('link[rel="canonical"]', () => Object.assign(document.createElement("link"), { rel: "canonical" }), "href", site.url + pathname);
    else document.head.querySelectorAll('link[rel="canonical"]').forEach((l) => l.remove());
  }, [page, pathname]);
}

/** Like a static site: a new page starts at the top, a link with #anchor lands on it. */
function useScrollOnNavigate() {
  const { pathname, hash } = useLocation();
  useEffect(() => {
    const target = hash && document.getElementById(decodeURIComponent(hash.slice(1)));
    if (target) target.scrollIntoView();
    else window.scrollTo?.(0, 0);
  }, [pathname, hash]);
}

/** TK-312: sections marked .reveal ease in the first time they come on screen. Only with JavaScript and motion allowed, so the prerendered page and
 *  "reduce motion" show everything at rest; what is already on screen when the page starts is never hidden. Opacity and transform only (no shift). */
function useReveal() {
  const { pathname } = useLocation();
  useEffect(() => {
    if (typeof IntersectionObserver === "undefined" || typeof matchMedia !== "function" || matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    const seen = new IntersectionObserver(
      (entries) =>
        entries.forEach((e) => {
          if (!e.isIntersecting) return;
          e.target.classList.add("is-in");
          seen.unobserve(e.target);
        }),
      { rootMargin: "0px 0px -12% 0px" },
    );
    document.querySelectorAll<HTMLElement>(".site .reveal:not(.is-in)").forEach((el) => {
      if (el.getBoundingClientRect().top < window.innerHeight) return;
      el.classList.add("is-waiting");
      seen.observe(el);
    });
    return () => seen.disconnect();
  }, [pathname]);
}

const header = ["/carniceria", "/colmado", "/deli", "/e-cf", "/precios"];
const footer = ["/guia-e-cf", "/comparar", "/glosario", "/descargar", "/certificacion", "/novedades", "/terminos", "/privacidad"];

function Menu({ paths }: { paths: string[] }) {
  return (
    <ul>
      {sitePages
        .filter((p) => paths.includes(p.path))
        .map((p) => (
          <li key={p.path}>
            <NavLink to={p.path} end>
              {p.nav}
            </NavLink>
          </li>
        ))}
    </ul>
  );
}

/** Public site frame (TK-228, TK-245): e-CF deadline banner, brand bar with the trial, menu that scrolls sideways on a phone, slim footer with the DGII disclaimer. */
export function SiteLayout() {
  const { pathname } = useLocation();
  useHead(pageFor(pathname), pathname);
  useScrollOnNavigate();
  useReveal();

  return (
    <div className="site">
      <a className="skip" href="#contenido">
        Saltar al contenido
      </a>
      <Link className="deadline" to="/e-cf">
        <strong>15 de noviembre de 2026:</strong> el e-CF llega a los pequeños negocios. <span>¿Ya le toca?</span>
      </Link>
      <header className="top">
        <div className="wrap top__bar">
          <Link className="brand" to="/">
            <Logo size={32} withName={false} />
            <span aria-hidden="true">abastalia</span>
          </Link>
          <nav className="menu" aria-label="Principal">
            <Menu paths={header} />
          </nav>
          <div className="top__actions">
            <Link className="top__login" to="/app">
              Entrar
            </Link>
            <Trial className="btn btn--small" />
          </div>
        </div>
      </header>
      <main id="contenido">
        <Outlet />
      </main>
      <footer className="foot">
        <div className="wrap foot__grid">
          <span>© 2026 Abastalia · Hecho en República Dominicana</span>
          <nav aria-label="Pie de página">
            <ul>
              {sitePages
                .filter((p) => footer.includes(p.path))
                .map((p) => (
                  <li key={p.path}>
                    <Link to={p.path}>{p.nav}</Link>
                  </li>
                ))}
              <li>
                <Link to="/app">Entrar</Link>
              </li>
            </ul>
          </nav>
          <ThemeToggle className="btn btn--ghost btn--small" size={18} lightFirst />
          <p className="muted foot__legal">La información sobre la DGII es orientativa y no sustituye la asesoría de su contador ni lo que publique la DGII.</p>
        </div>
      </footer>
      <SiteTalia />
    </div>
  );
}
