import { afterEach, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router";
import type { PanelContext } from "../App";
import type { StoreSyncStatus } from "../api";
import { json } from "../testing";
import { StoresPage } from "./StoresPage";

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

/** A business the cloud host does not serve: its route answers 404 (TK-321). */
const notHosted = (answer: (url: string, init?: RequestInit) => Promise<Response>) =>
  vi.fn(async (url: string, init?: RequestInit) => (url.includes("/api/panel/lite/") ? json(404, { code: "Lite.NotFound", detail: "No" }) : answer(url, init)));

const store = (nodes: number): StoreSyncStatus[] => [
  {
    storeId: "s1",
    storeName: "Carnicería La Esquina",
    lastEventUtc: null,
    lastSyncUtc: nodes ? new Date().toISOString() : null,
    pendingEvents: 0,
    failedEvents: 0,
    nodes: Array.from({ length: nodes }, (_, i) => ({ nodeId: `n${i}`, name: `Caja ${i + 1}`, lastEventUtc: null, lastSyncUtc: new Date().toISOString() })),
  },
];

it("TK-170: the list refreshes by itself when a register uses the connection code", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  let nodes = 0;
  vi.stubGlobal(
    "fetch",
    notHosted(async (url: string, init?: RequestInit) =>
      url.endsWith("/connection-codes") && init?.method === "POST"
        ? json(200, { code: "12345678", expiresUtc: new Date(Date.now() + 30 * 60_000).toISOString() })
        : json(200, url.endsWith("/backups") ? [] : store(nodes)),
    ),
  );
  const reloadStores = vi.fn();
  const context: PanelContext = { stores: [], reloadStores, storeId: "", setStoreId: () => {}, canEdit: true };
  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<StoresPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );

  fireEvent.click(await screen.findByRole("button", { name: /Conectar una caja/ }));
  expect(await screen.findByText("1234 5678")).toBeTruthy();

  nodes = 1; // the register connected
  await act(() => vi.advanceTimersByTimeAsync(5000));

  expect(await screen.findByText("Caja conectada. Sus ventas empiezan a llegar a este panel.")).toBeTruthy();
  expect(screen.getByText("Caja 1")).toBeTruthy();
  expect(screen.queryByText("1234 5678")).toBeNull();
  expect(reloadStores).toHaveBeenCalled();
});

it("TK-217: a read-only member sees the stores but cannot add one or connect a register", async () => {
  vi.stubGlobal("fetch", notHosted(async () => json(200, store(1))));
  const context: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {}, canEdit: false };
  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<StoresPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );

  expect(await screen.findByText("Caja 1")).toBeTruthy();
  expect(screen.queryByRole("button", { name: /Nueva tienda/ })).toBeNull();
  expect(screen.queryByRole("button", { name: /Conectar una caja/ })).toBeNull();
});

it("TK-241: an Owner removes a register after confirming, and the list reloads without it", async () => {
  let nodes = 1;
  const calls: string[] = [];
  vi.stubGlobal(
    "fetch",
    notHosted(async (url: string, init?: RequestInit) => {
      if (init?.method === "POST") {
        calls.push(url);
        nodes = 0;
        return json(204);
      }
      return json(200, url.endsWith("/backups") ? [] : store(nodes));
    }),
  );
  const context: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {}, canEdit: true };
  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<StoresPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Quitar" }));
  expect(calls).toEqual([]);
  fireEvent.click(screen.getByRole("button", { name: "Sí, quitar" }));
  expect(await screen.findByText("Ninguna caja conectada")).toBeTruthy();
  expect(calls).toHaveLength(1);
  expect(calls[0]).toMatch("/api/panel/nodes/n0/revoke");
});

function renderStores(canEdit = true) {
  const context: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {}, canEdit };
  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<StoresPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

it("TK-238, R12 #9: an Owner sees the store's backups and downloads one through a short-lived link, not through the page's memory", async () => {
  const backup = { id: "b1", fileName: "abastalia-20261014-233000.abastalia-backup", bytes: 52_428_800, createdUtc: "2026-10-15T03:30:00Z", uploadedUtc: "2026-10-15T03:40:00Z" };
  const fetch = notHosted(async (url: string, init?: RequestInit) =>
    url.endsWith("/backups/b1/link") && init?.method === "POST"
      ? json(200, { url: "/api/backups/download/token" })
      : json(200, url.endsWith("/backups") ? [backup] : store(1)),
  );
  vi.stubGlobal("fetch", fetch);
  const clicked: string[] = [];
  const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    clicked.push(this.href);
  });
  renderStores();

  expect(await screen.findByText("Respaldos en la nube")).toBeTruthy();
  expect(await screen.findByText(/50 MB/)).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Descargar" }));
  await vi.waitFor(() => expect(clicked).toHaveLength(1));
  expect(clicked[0]).toMatch(/\/api\/backups\/download\/token$/);
  click.mockRestore();
});

it("R12 #7: the backups show for an Owner even when the store has no register left (a stolen PC was removed)", async () => {
  vi.stubGlobal("fetch", notHosted(async (url: string) => json(200, url.endsWith("/backups") ? [] : store(0))));
  renderStores();
  expect(await screen.findByText("Respaldos en la nube")).toBeTruthy();
});

it("TK-321: a Lite business pairs its one selling device with a code, sees it join, and revokes it", async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  let devices: { id: string; name: string; createdUtc: string }[] = [];
  const posts: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === "POST") posts.push(url);
      if (url.endsWith("/api/panel/lite/device-codes")) return json(200, { code: "87654321", expiresUtc: new Date(Date.now() + 10 * 60_000).toISOString() });
      if (url.endsWith("/revoke")) return (devices = []), json(204);
      if (url.endsWith("/api/panel/lite/devices")) return json(200, { allowed: 1, devices });
      return json(200, url.endsWith("/backups") ? [] : store(1));
    }),
  );
  renderStores();

  fireEvent.click(await screen.findByRole("button", { name: /Unir un dispositivo/ }));
  expect(await screen.findByText("8765 4321")).toBeTruthy();
  devices = [{ id: "d1", name: "Celular", createdUtc: "2026-10-17T15:00:00Z" }]; // the browser used the code
  await act(() => vi.advanceTimersByTimeAsync(5000));
  expect(await screen.findByText("Dispositivo unido. Ya puede vender con él.")).toBeTruthy();
  expect(screen.getByText("Celular")).toBeTruthy();
  expect(screen.queryByRole("button", { name: /Unir un dispositivo/ })).toBeNull(); // one at a time

  fireEvent.click(screen.getByRole("button", { name: "Revocar" }));
  fireEvent.click(screen.getByRole("button", { name: "Sí, revocar" }));
  expect(await screen.findByRole("button", { name: /Unir un dispositivo/ })).toBeTruthy();
  expect(posts.map((p) => p.replace(/^.*\/api\//, "/api/"))).toEqual(["/api/panel/lite/device-codes", "/api/panel/lite/devices/d1/revoke"]);
});

it("TK-321: another plan shows no selling device, and a read-only member of a Lite business cannot pair or revoke", async () => {
  vi.stubGlobal("fetch", notHosted(async (url: string) => json(200, url.endsWith("/backups") ? [] : store(1))));
  renderStores();
  expect(await screen.findByText("Caja 1")).toBeTruthy();
  expect(screen.queryByText("Dispositivo de venta")).toBeNull();
  cleanup();

  const device = { id: "d1", name: "Celular", createdUtc: "2026-10-17T15:00:00Z" };
  vi.stubGlobal("fetch", vi.fn(async (url: string) => json(200, url.endsWith("/api/panel/lite/devices") ? { allowed: 1, devices: [device] } : store(1))));
  renderStores(false);
  expect(await screen.findByText("Celular")).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Revocar" })).toBeNull();
  expect(screen.queryByRole("button", { name: /Unir un dispositivo/ })).toBeNull();
  cleanup();

  // TK-347: a plan with two at a time still offers "Unir" after the first.
  vi.stubGlobal("fetch", vi.fn(async (url: string) => json(200, url.endsWith("/api/panel/lite/devices") ? { allowed: 2, devices: [device] } : url.endsWith("/backups") ? [] : store(1))));
  renderStores();
  expect(await screen.findByText("Su plan permite 2 celulares o computadoras a la vez; 1 en uso.")).toBeTruthy();
  expect(screen.getByRole("button", { name: /Unir un dispositivo/ })).toBeTruthy();
  cleanup();

  renderStores(false);
  expect(await screen.findByText("Celular")).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Revocar" })).toBeNull();
  expect(screen.queryByRole("button", { name: /Unir un dispositivo/ })).toBeNull();
});
