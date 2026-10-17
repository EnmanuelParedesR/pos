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
    vi.fn(async (url: string, init?: RequestInit) =>
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
  vi.stubGlobal("fetch", vi.fn(async () => json(200, store(1))));
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
    vi.fn(async (url: string, init?: RequestInit) => {
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

it("TK-238: an Owner sees the store's backups in the cloud and downloads one with the session", async () => {
  const backup = { id: "b1", fileName: "abastalia-20261014-233000.abastalia-backup", bytes: 52_428_800, createdUtc: "2026-10-15T03:30:00Z", uploadedUtc: "2026-10-15T03:40:00Z" };
  const fetch = vi.fn(async (url: string) =>
    url.endsWith("/backups/b1") ? new Response(new Blob(["TOKIRSP2"]), { status: 200 }) : json(200, url.endsWith("/backups") ? [backup] : store(1)),
  );
  vi.stubGlobal("fetch", fetch);
  const createObjectURL = vi.fn(() => "blob:respaldo");
  const revokeObjectURL = vi.fn();
  vi.stubGlobal("URL", Object.assign(Object.create(URL), { createObjectURL, revokeObjectURL }));
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

  expect(await screen.findByText("Respaldos en la nube")).toBeTruthy();
  expect(await screen.findByText(/50 MB/)).toBeTruthy();
  fireEvent.click(screen.getByRole("button", { name: "Descargar" }));
  await vi.waitFor(() => expect(createObjectURL).toHaveBeenCalled());
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/stores/s1/backups/b1"))).toBe(true);
});
