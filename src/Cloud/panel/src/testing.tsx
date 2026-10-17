import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router";
import type { PanelContext } from "./App";

/** A fake fetch response with only the parts the API wrapper reads. */
export const json = (status: number, body?: unknown) =>
  ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => (body === undefined ? Promise.reject(new SyntaxError("empty")) : body),
    text: async () => (body === undefined ? "" : JSON.stringify(body)),
  }) as Response;

/** TK-305: one page with the frame's context, at `path` matched by `route`, without booting the App (its site, lazy chunks and redirects).
 *  The shell itself: `renderPage(<Panel />, { path: "/app/sales", route: "app/*" })`. */
export function renderPage(page: ReactElement, { path = "/", route = "*", context = {} }: { path?: string; route?: string; context?: Partial<PanelContext> } = {}) {
  const frame: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {}, canEdit: true, ...context };
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route element={<Outlet context={frame} />}>
          <Route path={route} element={page} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}
