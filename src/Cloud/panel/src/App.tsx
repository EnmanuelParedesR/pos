import { lazy, Suspense } from "react";
import { Navigate, Route, Routes, useLocation, useOutletContext } from "react-router";
import type { Store } from "./api";
import { SiteLayout } from "./pages/site/SiteLayout";
import { notFound, sitePages } from "./pages/site/SitePages";

// TK-278: only the public site is in the main bundle (prerendered and hydrated); everything else downloads when its address opens.
const Panel = lazy(() => import("./Panel").then((m) => ({ default: m.Panel })));
const CatalogPage = lazy(() => import("./pages/CatalogPage").then((m) => ({ default: m.CatalogPage })));
const InvitePage = lazy(() => import("./pages/InvitePage").then((m) => ({ default: m.InvitePage })));
const UnsubscribePage = lazy(() => import("./pages/NotificationsPage").then((m) => ({ default: m.UnsubscribePage })));
const ConfirmEmailPage = lazy(() => import("./pages/EmailLinkPages").then((m) => ({ default: m.ConfirmEmailPage })));
const ResetPasswordPage = lazy(() => import("./pages/EmailLinkPages").then((m) => ({ default: m.ResetPasswordPage })));

/** What every screen gets from the frame: the tenant's stores, the selected one ("" = all) and whether this member may change things (TK-217: Owner). */
export interface PanelContext {
  stores: Store[];
  reloadStores: () => void;
  storeId: string;
  setStoreId: (id: string) => void;
  canEdit: boolean;
}

export const usePanel = () => useOutletContext<PanelContext>();

/** Signed-in screens live under /app (TK-228); these older addresses forward there so saved links and older e-mails keep working. */
export const movedToApp = ["sales", "shifts", "stores", "assistant", "received", "notifications", "members", "platform"];

function ToApp() {
  const { pathname, search, hash } = useLocation();
  return <Navigate replace to={`/app${pathname}${search}${hash}`} />;
}

/** One app, two halves (TK-228): the public site and the pages a link opens without a session at the root, the owner's panel under /app. */
export function App() {
  // The site routes never suspend, so the fallback never replaces a prerendered page.
  return (
    <Suspense fallback={null}>
      <Routes>
        <Route element={<SiteLayout />}>
          {sitePages.map((p) => (
            <Route key={p.path} path={p.path} element={p.element} />
          ))}
          <Route path="*" element={notFound.element} />
        </Route>
        {/* The public catalog (TK-195): a store's customers open it from a QR. */}
        <Route path="c/:storeId" element={<CatalogPage />} />
        {/* TK-217: the invite link works signed in or not; the page itself asks for the account. R8 #12: the token is after "#" (/invite#token). */}
        <Route path="invite" element={<InvitePage />} />
        <Route path="invite/:token" element={<InvitePage />} />
        {/* TK-222: the unsubscribe link of an alert e-mail works without a session. */}
        <Route path="unsubscribe" element={<UnsubscribePage />} />
        {/* TK-230: the links of the "¿La olvidó?" and sign-up e-mails; the code is after "#" (R8 #12). */}
        <Route path="app/restablecer" element={<ResetPasswordPage />} />
        <Route path="app/confirmar" element={<ConfirmEmailPage />} />
        <Route path="app/*" element={<Panel />} />
        {movedToApp.map((p) => (
          <Route key={p} path={`${p}/*`} element={<ToApp />} />
        ))}
      </Routes>
    </Suspense>
  );
}

