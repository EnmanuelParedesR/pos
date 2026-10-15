import { afterEach, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router";
import type { PanelContext } from "../App";
import type { Summary } from "../api";
import { json } from "../testing";
import { SummaryPage } from "./SummaryPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const summary: Summary = {
  date: "2026-10-15",
  salesTotal: 15234.5,
  salesCount: 42,
  averageTicket: 362.73,
  discountTotal: 0,
  itbisTotal: 1200,
  returnsTotal: 300,
  netTotal: 14934.5,
  voidedCount: 2,
  byHour: [
    { hour: 8, count: 10, total: 4000 },
    { hour: 10, count: 32, total: 11234.5 },
  ],
  paymentMix: [
    { method: "Cash", count: 30, amount: 10000 },
    { method: "OnAccount", count: 12, amount: 5234.5 },
  ],
  issues: [
    {
      kind: "ShiftDifference",
      storeId: "s1",
      storeName: "Centro",
      referenceId: "z1",
      amount: -200,
      occurredUtc: "2026-10-15T20:00:00Z",
      message: "Z #3: faltante de RD$ 200.00",
    },
    {
      kind: "SyncStale",
      storeId: "s1",
      storeName: "Centro",
      referenceId: null,
      amount: null,
      occurredUtc: null,
      message: "La tienda no ha enviado datos en más de 24 horas.",
    },
  ],
  lastEventUtc: new Date(Date.now() - 5 * 60_000).toISOString(),
};

it("shows the day's numbers, payment methods in Spanish and what to review", async () => {
  const fetch = vi.fn(async (_url: string) => json(200, summary));
  vi.stubGlobal("fetch", fetch);
  const context: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {} };

  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<SummaryPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );

  expect(await screen.findByText("RD$ 15,234.50")).toBeTruthy();
  expect(screen.getByText("42")).toBeTruthy();
  expect(screen.getByText("Efectivo")).toBeTruthy();
  expect(screen.getByText("Fiado")).toBeTruthy();
  expect(screen.getByText("Faltaron RD$ 200.00")).toBeTruthy();
  expect(screen.getByText("La tienda no ha enviado datos en más de 24 horas.")).toBeTruthy();
  expect(screen.getByText("Última actualización hace 5 min")).toBeTruthy();
  // 8, 9 (no sales) and 10.
  expect(screen.getByRole("img", { name: "Ventas por hora" }).children).toHaveLength(3);
  expect(fetch.mock.calls[0][0]).toMatch(/\/api\/panel\/summary\?date=\d{4}-\d{2}-\d{2}$/);
});
