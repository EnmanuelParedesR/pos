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
        : json(200, store(nodes)),
    ),
  );
  const reloadStores = vi.fn();
  const context: PanelContext = { stores: [], reloadStores, storeId: "", setStoreId: () => {} };
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
