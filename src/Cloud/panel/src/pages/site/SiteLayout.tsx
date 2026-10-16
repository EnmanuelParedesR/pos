import { useEffect } from "react";
import { Link, NavLink, Outlet, useLocation } from "react-router";
import { Logo, ThemeToggle } from "../../ui";
import { contactHref, site } from "./config";
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
    if (site.url) set('link[rel="canonical"]', () => Object.assign(document.createElement("link"), { rel: "canonical" }), "href", site.url + pathname);
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

function Menu({ withPanel = false }: { withPanel?: boolean }) {
  return (
    <ul>
      {sitePages.map((p) => (
        <li key={p.path}>
          <NavLink to={p.path} end>
            {p.nav}
          </NavLink>
        </li>
      ))}
      {withPanel && (
        <li>
          <Link to="/app">Entrar al panel</Link>
        </li>
      )}
    </ul>
  );
}

/** Public site frame (TK-228): brand bar with one call to action, menu that scrolls sideways on a phone, footer with the DGII disclaimer. */
export function SiteLayout() {
  const { pathname } = useLocation();
  useHead(pageFor(pathname), pathname);
  useScrollOnNavigate();
  const external = !contactHref.startsWith("/");

  return (
    <div className="site">
      <a className="skip" href="#contenido">
        Saltar al contenido
      </a>
      <header className="top">
        <div className="wrap top__bar">
          <Link className="brand" to="/">
            <Logo size={32} withName={false} />
            <span aria-hidden="true">toki</span>
          </Link>
          <div className="top__actions">
            <Link className="btn btn--ghost btn--small" to="/app">
              Entrar
            </Link>
            {external ? (
              <a className="btn btn--small" href={contactHref} rel="noopener">
                Hablemos
              </a>
            ) : (
              <Link className="btn btn--small" to={contactHref}>
                Hablemos
              </Link>
            )}
          </div>
        </div>
        <nav className="wrap menu" aria-label="Principal">
          <Menu />
        </nav>
      </header>
      <main id="contenido">
        <Outlet />
      </main>
      <footer className="foot">
        <div className="wrap foot__grid">
          <p>
            <strong>Toki</strong> · Punto de venta para carnicerías, colmados y delis en República Dominicana. Vende sin internet y emite e-CF.
          </p>
          <nav aria-label="Pie de página">
            <Menu withPanel />
          </nav>
          <p className="muted">La información sobre la DGII es orientativa y no sustituye la asesoría de tu contador ni lo que publique la DGII.</p>
          <div>
            <ThemeToggle className="btn btn--ghost btn--small" size={18} />
          </div>
        </div>
      </footer>
    </div>
  );
}
