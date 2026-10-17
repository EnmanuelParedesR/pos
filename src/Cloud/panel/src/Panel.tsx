// TK-278: the owner's panel, in its own chunk so the public pages do not download it (App.tsx loads it lazily).
import { useEffect, useRef, useState, type FormEvent } from "react";
import { NavLink, Outlet, Route, Routes, useLocation, useNavigate } from "react-router";
import { api, hasSession, renewSession, setOnSignedOut, signOut, useApi, roleNames, type Me, type Store } from "./api";
import { Icon, Logo, Talia, ThemeToggle, type IconName } from "./ui";
import type { PanelContext } from "./App";
import { SignIn } from "./pages/SignIn";
import { ConfirmEmailBanner } from "./pages/ConfirmEmailBanner";
import { SummaryPage } from "./pages/SummaryPage";
import { SalesPage } from "./pages/SalesPage";
import { ShiftsPage } from "./pages/ShiftsPage";
import { ShiftReportPage } from "./pages/ShiftReportPage";
import { StoresPage } from "./pages/StoresPage";
import { AssistantChat, AssistantPage, type PendingQuestion } from "./pages/AssistantPage";
import { TaliaPopup } from "./TaliaPopup";
import { BillingPage } from "./pages/BillingPage";
import { PlatformAccompanimentPage, PlatformDelegationsPage, PlatformMetricsPage, PlatformReleasesPage, PlatformStorePage, PlatformTenantPage, PlatformTenantsPage, PlatformWatchPage } from "./pages/PlatformPages";
import { SecurityPage } from "./pages/SecurityPage";
import { MembersPage } from "./pages/MembersPage";
import { NotificationsPage } from "./pages/NotificationsPage";
import { ReceivedPage } from "./pages/ReceivedPage";

/** The owner's panel: asks for the account, then shows the shell. */
export function Panel() {
  // undefined = checking a saved session, null = signed out.
  const [me, setMe] = useState<Me | null | undefined>(hasSession() ? undefined : null);

  useEffect(() => {
    document.title = "Abastalia · Panel";
    setOnSignedOut(() => setMe(null));
    if (me === undefined) api<Me>("/api/me").then(setMe, () => setMe(null));
    // Only on start: later sign-ins set `me` themselves.
  }, []);

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
        <Route path="received" element={<ReceivedPage />} />
        <Route path="notifications" element={<NotificationsPage />} />
        <Route path="security" element={<SecurityPage />} />
        {me.role === "Owner" && <Route path="members" element={<MembersPage me={me} />} />}
        {me.role === "Owner" && <Route path="billing" element={<BillingPage />} />}
        {/* Abastalia staff only (TK-189); the API checks the role on every call anyway. */}
        {me.isPlatformAdmin && (
          <>
            <Route path="platform" element={<PlatformMetricsPage />} />
            <Route path="platform/tenants" element={<PlatformTenantsPage />} />
            <Route path="platform/tenants/:tenantId" element={<PlatformTenantPage />} />
            <Route path="platform/stores/:storeId" element={<PlatformStorePage />} />
            <Route path="platform/accompaniment" element={<PlatformAccompanimentPage />} />
            <Route path="platform/releases" element={<PlatformReleasesPage />} />
            <Route path="platform/delegations" element={<PlatformDelegationsPage />} />
            <Route path="platform/health" element={<PlatformWatchPage />} />
          </>
        )}
        <Route path="*" element={<SummaryPage />} />
      </Route>
    </Routes>
  );
}

const sections: { to: string; label: string; icon: IconName }[] = [
  { to: "/app", label: "Resumen", icon: "dashboard" },
  { to: "/app/sales", label: "Ventas", icon: "chart" },
  { to: "/app/shifts", label: "Cuadres", icon: "wallet" },
  { to: "/app/stores", label: "Tiendas", icon: "store" },
  { to: "/app/assistant", label: "Asistente", icon: "sparkles" },
  { to: "/app/received", label: "Recibidos", icon: "inbox" },
  { to: "/app/notifications", label: "Avisos", icon: "bell" },
];

const membersSection = { to: "/app/members", label: "Equipo", icon: "users" as IconName };
const billingSection = { to: "/app/billing", label: "Facturación", icon: "receipt" as IconName }; // TK-249
const platformSection = { to: "/app/platform", label: "Plataforma", icon: "shield" as IconName };


const collapsedKey = "abastalia.menuCollapsed";

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
  const nav = [...sections, ...(canEdit ? [membersSection, billingSection] : []), ...(me.isPlatformAdmin ? [platformSection] : [])];

  // TK-217: a member of several businesses picks one; the new token carries it.
  const switchTenant = async (tenantId: string) => {
    await api("/api/me/tenant", "POST", { tenantId });
    await renewSession();
    onMe(await api<Me>("/api/me"));
    navigate("/app");
  };
  const onPlatform = pathname.startsWith("/app/platform");
  const onAssistant = pathname === "/app/assistant";
  const [taliaOpen, setTaliaOpen] = useState(false);
  const [pending, setPending] = useState<PendingQuestion | null>(null);
  const openTalia = (question: string) => {
    if (question) setPending({ text: question, id: Date.now() });
    setTaliaOpen(true);
  };

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
            <NavLink key={s.to} to={s.to} end={s.to === "/app"} title={s.label}>
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
              {me.isPlatformAdmin ? "Equipo de Abastalia" : roleNames[me.role]}
            </div>
          </div>
          <ThemeToggle />
          <NavLink to="/app/security" className="esc-side__item" title="Seguridad">
            <Icon name="shield" />
            <span className="esc-side__label">Seguridad</span>
          </NavLink>
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
        <NavLink to="/app/security" className="esc-button esc-button--ghost esc-button--small" aria-label="Seguridad">
          <Icon name="shield" size={18} />
        </NavLink>
        <button type="button" className="esc-button esc-button--ghost esc-button--small" aria-label="Salir" onClick={signOut}>
          <Icon name="sign-out" size={18} />
        </button>
      </header>

      <main className="esc-main">
        <div className="esc-page">
          <ConfirmEmailBanner me={me} />
          {!onAssistant && !onPlatform && <AskBar onAsk={openTalia} />}
          <Outlet context={context} />
        </div>
      </main>

      <TabBar nav={nav} />
      {/* Every panel member may use the assistant (the API's panel policy); on its own page the popup would be the same chat twice. */}
      {!onAssistant && <PanelTalia open={taliaOpen} onOpen={() => setTaliaOpen(true)} onClose={() => setTaliaOpen(false)} pending={pending} onAsked={() => setPending(null)} />}
    </div>
  );
}

/** TK-299: the phone's bottom bar fits five tabs; past that the fifth is "Más", a sheet with the rest (like Escuadra's Admin.razor). */
function TabBar({ nav }: { nav: { to: string; label: string; icon: IconName }[] }) {
  const { pathname } = useLocation();
  // Open only on the address where it was opened, so moving to a section closes it (TK-305: no closing effect racing the first tap).
  const [openAt, setOpenAt] = useState<string | null>(null);
  const open = openAt === pathname;
  const setOpen = (value: boolean) => setOpenAt(value ? pathname : null);
  const bar = useRef<HTMLElement>(null);
  const tabs = nav.length > 5 ? nav.slice(0, 4) : nav;
  const rest = nav.slice(tabs.length);
  const inRest = rest.some((s) => pathname.startsWith(s.to));

  useEffect(() => {
    if (!open) return;
    const outside = (e: PointerEvent) => bar.current?.contains(e.target as Node) || setOpen(false);
    const esc = (e: globalThis.KeyboardEvent) => e.key === "Escape" && setOpen(false);
    document.addEventListener("pointerdown", outside);
    document.addEventListener("keydown", esc);
    return () => {
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("keydown", esc);
    };
  }, [open]);

  return (
    <nav className="esc-tabbar" aria-label="Secciones" ref={bar}>
      {tabs.map((s) => (
        <NavLink key={s.to} to={s.to} end={s.to === "/app"}>
          <Icon name={s.icon} size={22} />
          <span className="esc-tabbar__label">{s.label}</span>
        </NavLink>
      ))}
      {rest.length > 0 && (
        <>
          <button type="button" className={`esc-tabbar__more${inRest ? " active" : ""}`} aria-expanded={open} aria-controls="more-sections" onClick={() => setOpen(!open)}>
            <Icon name="menu" size={22} />
            <span className="esc-tabbar__label">Más</span>
          </button>
          {open && (
            <div className="esc-tabbar__sheet" id="more-sections">
              {rest.map((s) => (
                <NavLink key={s.to} to={s.to}>
                  <Icon name={s.icon} />
                  {s.label}
                </NavLink>
              ))}
            </div>
          )}
        </>
      )}
    </nav>
  );
}

/** TK-300: Talia one tap away on every screen (TaliaPopup.tsx). A link in an answer (TK-301): on the phone the sheet covers the screen, so it closes to show where it went. */
function PanelTalia({ open, onOpen, onClose, pending, onAsked }: { open: boolean; onOpen: () => void; onClose: () => void; pending: PendingQuestion | null; onAsked: () => void }) {
  return (
    <TaliaPopup open={open} onOpen={onOpen} onClose={onClose} subtitle="Su asistente">
      <AssistantChat pending={pending} onAsked={onAsked} inputId="talia-question" inPopup onNavigate={() => window.matchMedia?.("(max-width: 760px)").matches && onClose()} />
    </TaliaPopup>
  );
}

/** "Pregúntale a Talia…": the assistant one tap away from every screen (JTBD 7). The question goes to Talia's popup. */
function AskBar({ onAsk }: { onAsk: (question: string) => void }) {
  const [question, setQuestion] = useState("");
  const submit = (e: FormEvent) => {
    e.preventDefault();
    onAsk(question.trim());
    setQuestion("");
  };
  return (
    <form className="esc-ask" onSubmit={submit} role="search">
      <Talia size={26} />
      <label className="esc-visually-hidden" htmlFor="ask">
        Pregúntale a Talia
      </label>
      <input id="ask" className="esc-ask__input" placeholder="Pregúntale a Talia… ¿cuánto vendí ayer?" value={question} onChange={(e) => setQuestion(e.target.value)} />
      <button type="submit" className="esc-button esc-button--small esc-button--primary" aria-label="Preguntar">
        <Icon name="send" size={16} />
      </button>
    </form>
  );
}
