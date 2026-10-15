import { useEffect, useState } from "react";
import { NavLink, Outlet, Route, Routes, useOutletContext } from "react-router";
import { api, hasSession, setOnSignedOut, signOut, useApi, type Me, type Store } from "./api";
import { Logo } from "./ui";
import { SignIn } from "./pages/SignIn";
import { SummaryPage } from "./pages/SummaryPage";
import { SalesPage } from "./pages/SalesPage";
import { ShiftsPage } from "./pages/ShiftsPage";
import { StoresPage } from "./pages/StoresPage";
import { AssistantPage } from "./pages/AssistantPage";

/** What every screen gets from the frame: the tenant's stores and the selected one ("" = all). */
export interface PanelContext {
  stores: Store[];
  reloadStores: () => void;
  storeId: string;
  setStoreId: (id: string) => void;
}

export const usePanel = () => useOutletContext<PanelContext>();

export function App() {
  // undefined = checking a saved session, null = signed out.
  const [me, setMe] = useState<Me | null | undefined>(hasSession() ? undefined : null);

  useEffect(() => {
    setOnSignedOut(() => setMe(null));
    if (me === undefined) api<Me>("/api/me").then(setMe, () => setMe(null));
    // Only on start: later sign-ins set `me` themselves.
  }, []);

  if (me === undefined) return <p className="esc-muted empty">Cargando…</p>;
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

const sections = [
  { to: "/", label: "Resumen" },
  { to: "/sales", label: "Ventas" },
  { to: "/shifts", label: "Cuadres" },
  { to: "/stores", label: "Tiendas" },
  { to: "/assistant", label: "Asistente" },
];

function Shell({ me }: { me: Me }) {
  const stores = useApi<Store[]>("/api/stores");
  const [storeId, setStoreId] = useState("");
  const context: PanelContext = { stores: stores.data ?? [], reloadStores: stores.reload, storeId, setStoreId };

  return (
    <div className="shell">
      <header className="topbar">
        <Logo onFrame />
        <span className="topbar__tenant">{me.tenantName}</span>
        <button type="button" className="esc-button esc-button--ghost topbar__exit" onClick={signOut}>
          Salir
        </button>
      </header>
      <nav className="nav" aria-label="Secciones">
        {sections.map((s) => (
          <NavLink key={s.to} to={s.to} end={s.to === "/"}>
            {s.label}
          </NavLink>
        ))}
      </nav>
      <main className="page">
        <Outlet context={context} />
      </main>
    </div>
  );
}
