import { useEffect, useState, type FormEvent } from "react";
import { NavLink, Outlet, Route, Routes, useLocation, useNavigate, useOutletContext } from "react-router";
import { api, hasSession, renewSession, setOnSignedOut, signOut, useApi, roleNames, type Me, type Store } from "./api";
import { Icon, Logo, ThemeToggle, type IconName } from "./ui";
import { SignIn } from "./pages/SignIn";
import { SummaryPage } from "./pages/SummaryPage";
import { SalesPage } from "./pages/SalesPage";
import { ShiftsPage } from "./pages/ShiftsPage";
import { ShiftReportPage } from "./pages/ShiftReportPage";
import { StoresPage } from "./pages/StoresPage";
import { AssistantPage } from "./pages/AssistantPage";
import { PlatformMetricsPage, PlatformStorePage, PlatformTenantPage, PlatformTenantsPage } from "./pages/PlatformPages";
import { CatalogPage } from "./pages/CatalogPage";
import { MembersPage } from "./pages/MembersPage";
import { InvitePage } from "./pages/InvitePage";

/** What every screen gets from the frame: the tenant's stores, the selected one ("" = all) and whether this member may change things (TK-217: Owner). */
export interface PanelContext {
  stores: Store[];
  reloadStores: () => void;
  storeId: string;
  setStoreId: (id: string) => void;
  canEdit: boolean;
}

export const usePanel = () => useOutletContext<PanelContext>();

export function App() {
  // The public catalog (TK-195) needs no session: a store's customers open it from a QR.
  const { pathname } = useLocation();
  const isPublic = pathname.startsWith("/c/");
  // TK-217: the invite link works signed in or not; the page itself asks for the account. R8 #12: the token is after "#" (/invite#token).
  const isInvite = pathname === "/invite" || pathname.startsWith("/invite/");
  // undefined = checking a saved session, null = signed out.
  const [me, setMe] = useState<Me | null | undefined>(hasSession() && !isPublic && !isInvite ? undefined : null);

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

  if (isInvite)
    return (
      <Routes>
        <Route path="/invite" element={<InvitePage onJoined={setMe} />} />
        <Route path="/invite/:token" element={<InvitePage onJoined={setMe} />} />
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
      <Route element={<Shell key={me.tenantId} me={me} onMe={setMe} />}>
        <Route index element={<SummaryPage />} />
        <Route path="sales" element={<SalesPage />} />
        <Route path="shifts" element={<ShiftsPage />} />
        <Route path="shifts/:shiftId" element={<ShiftReportPage />} />
        <Route path="stores" element={<StoresPage />} />
        <Route path="assistant" element={<AssistantPage />} />
        {me.role === "Owner" && <Route path="members" element={<MembersPage me={me} />} />}
        {/* Toki staff only (TK-189); the API checks the role on every call anyway. */}
        {me.isPlatformAdmin && (
          <>
            <Route path="platform" element={<PlatformMetricsPage />} />
            <Route path="platform/tenants" element={<PlatformTenantsPage />} />
            <Route path="platform/tenants/:tenantId" element={<PlatformTenantPage />} />
            <Route path="platform/stores/:storeId" element={<PlatformStorePage />} />
          </>
        )}
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

const membersSection = { to: "/members", label: "Equipo", icon: "users" as IconName };
const platformSection = { to: "/platform", label: "Plataforma", icon: "shield" as IconName };


const collapsedKey = "toki.menuCollapsed";

function readCollapsed() {
  try {
    return localStorage.getItem(collapsedKey) === "1";
  } catch {
    return false;
  }
}

/** Escuadra Admin shell: icon menu on the left (collapsible) on a computer, top bar and bottom tabs on the phone. */
function Shell({ me, onMe }: { me: Me; onMe: (me: Me) => void }) {
  const stores = useApi<Store[]>("/api/stores");
  const [storeId, setStoreId] = useState("");
  const [collapsed, setCollapsed] = useState(readCollapsed);
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const canEdit = me.role === "Owner";
  const context: PanelContext = { stores: stores.data ?? [], reloadStores: stores.reload, storeId, setStoreId, canEdit };
  const nav = [...sections, ...(canEdit ? [membersSection] : []), ...(me.isPlatformAdmin ? [platformSection] : [])];

  // TK-217: a member of several businesses picks one; the new token carries it.
  const switchTenant = async (tenantId: string) => {
    await api("/api/me/tenant", "POST", { tenantId });
    await renewSession();
    onMe(await api<Me>("/api/me"));
    navigate("/");
  };
  const onPlatform = pathname.startsWith("/platform");

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
          <b>{me.tenantName}</b>
          {me.isPlatformAdmin ? "Panel y plataforma" : canEdit ? "Panel del dueño" : "Panel de solo lectura"}
          {me.tenants.length > 1 && (
            <select className="esc-select" aria-label="Negocio" value={me.tenantId} onChange={(e) => void switchTenant(e.target.value).catch(() => undefined)}>
              {me.tenants.map((t) => (
                <option key={t.tenantId} value={t.tenantId}>
                  {t.name}
                </option>
              ))}
            </select>
          )}
        </div>
        <nav className="esc-side__nav" aria-label="Secciones">
          {nav.map((s) => (
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
              <b>{me.email ?? me.tenantName}</b>
              {me.isPlatformAdmin ? "Equipo de Toki" : roleNames[me.role]}
            </div>
          </div>
          <ThemeToggle />
          <button type="button" className="esc-side__item" title="Salir" onClick={signOut}>
            <Icon name="sign-out" />
            <span className="esc-side__label">Salir</span>
          </button>
        </div>
      </aside>

      <header className="esc-topbar">
        <Logo size={26} />
        <span className="esc-topbar__store">{me.tenantName}</span>
        <ThemeToggle className="esc-button esc-button--ghost esc-button--small" showLabel={false} size={18} />
        <button type="button" className="esc-button esc-button--ghost esc-button--small" aria-label="Salir" onClick={signOut}>
          <Icon name="sign-out" size={18} />
        </button>
      </header>

      <main className="esc-main">
        <div className="esc-page">
          {pathname !== "/assistant" && !onPlatform && <AskBar />}
          <Outlet context={context} />
        </div>
      </main>

      <nav className="esc-tabbar" aria-label="Secciones">
        {nav.map((s) => (
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
