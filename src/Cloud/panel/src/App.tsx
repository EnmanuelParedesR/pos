import { useEffect, useState, type FormEvent } from "react";
import { NavLink, Outlet, Route, Routes, useLocation, useNavigate, useOutletContext } from "react-router";
import { api, hasSession, setOnSignedOut, signOut, useApi, type Me, type Store } from "./api";
import { Icon, Logo, type IconName } from "./ui";
import { SignIn } from "./pages/SignIn";
import { SummaryPage } from "./pages/SummaryPage";
import { SalesPage } from "./pages/SalesPage";
import { ShiftsPage } from "./pages/ShiftsPage";
import { StoresPage } from "./pages/StoresPage";
import { AssistantPage } from "./pages/AssistantPage";
import { CatalogPage } from "./pages/CatalogPage";

/** What every screen gets from the frame: the tenant's stores and the selected one ("" = all). */
export interface PanelContext {
  stores: Store[];
  reloadStores: () => void;
  storeId: string;
  setStoreId: (id: string) => void;
}

export const usePanel = () => useOutletContext<PanelContext>();

export function App() {
  // The public catalog (TK-195) needs no session: a store's customers open it from a QR.
  const isPublic = useLocation().pathname.startsWith("/c/");
  // undefined = checking a saved session, null = signed out.
  const [me, setMe] = useState<Me | null | undefined>(hasSession() && !isPublic ? undefined : null);

  useEffect(() => {
    setOnSignedOut(() => setMe(null));
    if (me === undefined) api<Me>("/api/me").then(setMe, () => setMe(null));
    // Only on start: later sign-ins set `me` themselves.
  }, []);

  if (isPublic)
    return (
      <Routes>
        <Route path="/c/:storeId" element={<CatalogPage />} />
      </Routes>
    );

  if (me === undefined)
    return (
      <div className="esc-admin" style={{ minHeight: "100vh", display: "grid", placeItems: "center" }}>
        <Logo size={44} />
      </div>
    );
  if (me === null) return <SignIn onSignedIn={() => api<Me>("/api/me").then(setMe)} />;

  return (
    <Routes>
      <Route element={<Shell me={me} />}>
        <Route index element={<SummaryPage />} />
        <Route path="sales" element={<SalesPage />} />
        <Route path="shifts" element={<ShiftsPage />} />
        <Route path="stores" element={<StoresPage />} />
        <Route path="assistant" element={<AssistantPage />} />
        <Route path="*" element={<SummaryPage />} />
      </Route>
    </Routes>
  );
}

const sections: { to: string; label: string; icon: IconName }[] = [
  { to: "/", label: "Resumen", icon: "dashboard" },
  { to: "/sales", label: "Ventas", icon: "chart" },
  { to: "/shifts", label: "Cuadres", icon: "wallet" },
  { to: "/stores", label: "Tiendas", icon: "store" },
  { to: "/assistant", label: "Asistente", icon: "sparkles" },
];

const collapsedKey = "toki.menuCollapsed";

function readCollapsed() {
  try {
    return localStorage.getItem(collapsedKey) === "1";
  } catch {
    return false;
  }
}

/** Escuadra Admin shell: icon menu on the left (collapsible) on a computer, top bar and bottom tabs on the phone. */
function Shell({ me }: { me: Me }) {
  const stores = useApi<Store[]>("/api/stores");
  const [storeId, setStoreId] = useState("");
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const { pathname } = useLocation();
  const context: PanelContext = { stores: stores.data ?? [], reloadStores: stores.reload, storeId, setStoreId };

  const toggle = () => {
    setCollapsed(!collapsed);
    try {
      localStorage.setItem(collapsedKey, collapsed ? "0" : "1");
    } catch {
      // Blocked storage: the menu just forgets on reload.
    }
  };

  return (
    <div className={`esc-admin esc-shell${collapsed ? " esc-shell--collapsed" : ""}`}>
      <aside className="esc-side">
        <div className="esc-side__brand">
          <Logo size={28} />
          <span className="esc-spacer" />
          <button type="button" className="esc-side__item esc-side__toggle" aria-label={collapsed ? "Mostrar el menú" : "Contraer el menú"} title={collapsed ? "Mostrar el menú" : "Contraer el menú"} onClick={toggle}>
            <Icon name="panel" size={18} />
          </button>
        </div>
        <div className="esc-side__store">
          <b>{me.tenantName}</b>Panel del dueño
        </div>
        <nav className="esc-side__nav" aria-label="Secciones">
          {sections.map((s) => (
            <NavLink key={s.to} to={s.to} end={s.to === "/"} title={s.label}>
              <Icon name={s.icon} />
              <span className="esc-side__label">{s.label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="esc-side__foot">
          <div className="esc-side__user">
            <span className="esc-avatar" aria-hidden="true">
              {(me.email ?? me.tenantName).slice(0, 1).toUpperCase()}
            </span>
            <div>
              <b>{me.email ?? me.tenantName}</b>Dueño
            </div>
          </div>
          <button type="button" className="esc-side__item" title="Salir" onClick={signOut}>
            <Icon name="sign-out" />
            <span className="esc-side__label">Salir</span>
          </button>
        </div>
      </aside>

      <header className="esc-topbar">
        <Logo size={26} />
        <span className="esc-topbar__store">{me.tenantName}</span>
        <button type="button" className="esc-button esc-button--ghost esc-button--small" aria-label="Salir" onClick={signOut}>
          <Icon name="sign-out" size={18} />
        </button>
      </header>

      <main className="esc-main">
        <div className="esc-page">
          {pathname !== "/assistant" && <AskBar />}
          <Outlet context={context} />
        </div>
      </main>

      <nav className="esc-tabbar" aria-label="Secciones">
        {sections.map((s) => (
          <NavLink key={s.to} to={s.to} end={s.to === "/"}>
            <Icon name={s.icon} size={22} />
            {s.label}
          </NavLink>
        ))}
      </nav>
    </div>
  );
}

/** "Pregúntale a Toki…": the assistant one tap away from every screen (JTBD 7). The question travels in router state, not in the URL. */
function AskBar() {
  const navigate = useNavigate();
  const [question, setQuestion] = useState("");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    navigate("/assistant", { state: question.trim() ? { question: question.trim() } : null });
  };
  return (
    <form className="esc-ask" onSubmit={submit} role="search">
      <Icon name="sparkles" />
      <label className="esc-visually-hidden" htmlFor="ask">
        Pregúntale a Toki
      </label>
      <input id="ask" className="esc-ask__input" placeholder="Pregúntale a Toki… ¿cuánto vendí ayer?" value={question} onChange={(e) => setQuestion(e.target.value)} />
      <button type="submit" className="esc-button esc-button--small esc-button--primary" aria-label="Preguntar">
        <Icon name="send" size={16} />
      </button>
    </form>
  );
}
