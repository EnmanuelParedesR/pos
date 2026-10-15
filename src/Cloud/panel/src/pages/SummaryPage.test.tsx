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
      cashierName: "Yudelka Martínez",
      registerName: "Caja 2",
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
  const fetch = vi.fn(async (url: string) =>
    url.includes("/sales/by-day")
      ? json(200, [{ date: "2026-10-14", count: 30, total: 12000, discount: 0, itbis: 900, returns: 0, net: 11100, voidedCount: 0 }])
      : url.includes("/sales/top-products")
        ? json(200, [{ productId: "p1", description: "Pechuga de pollo", quantity: 26.5, total: 4374.15, profit: 900, unit: "Pound" }])
        : url.includes("/receivables")
          ? json(200, {
              total: 1700,
              customerCount: 2,
              topDebtors: [
                { customerId: "c1", storeId: "s1", debt: 1500, customerName: "Ana Pérez" },
                { customerId: "0199e3a1-0000-7000-8000-00000000c3d4", storeId: "s1", debt: 200, customerName: null },
              ],
            })
          : json(200, summary),
  );
  vi.stubGlobal("fetch", fetch);
  const context: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {}, canEdit: true };

  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<SummaryPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );

  // The hero shows the amount big, with "RD$" apart.
  expect(await screen.findByText("15,234.50")).toBeTruthy();
  expect(screen.getByText("42")).toBeTruthy();
  expect(screen.getByText("Efectivo")).toBeTruthy();
  expect(screen.getByText("Fiado")).toBeTruthy();
  // What to review comes in words, with the amount.
  expect(screen.getByText((_, el) => el?.className === "esc-alert__title" && el.textContent === "Faltaron RD$ 200.00 en Caja 2")).toBeTruthy();
  expect(screen.getByText(/^Yudelka Martínez · Centro · cierre de/)).toBeTruthy();
  expect(screen.getByText("La tienda no ha enviado datos en más de 24 horas.")).toBeTruthy();
  expect(screen.getByText("Actualizado hace 5 min")).toBeTruthy();
  expect(await screen.findByText("Pechuga de pollo")).toBeTruthy();
  expect(screen.getByText("26.5 lb")).toBeTruthy(); // TK-180
  // TK-174: who owes, by name (or by the end of the id while an older store does not send it).
  expect(await screen.findByText("Ana Pérez")).toBeTruthy();
  expect(screen.getByText("Cliente …c3d4")).toBeTruthy();
  // 8, 9 (no sales) and 10.
  expect(screen.getByRole("list", { name: "Ventas por hora" }).querySelectorAll("[role=listitem]")).toHaveLength(3);
  expect(fetch.mock.calls[0][0]).toMatch(/\/api\/panel\/summary\?date=\d{4}-\d{2}-\d{2}$/);
});