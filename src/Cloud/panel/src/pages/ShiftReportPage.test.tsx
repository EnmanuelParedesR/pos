import { afterEach, expect, it, vi } from "vitest";
import type { ReactElement } from "react";
import { cleanup, fireEvent, screen } from "@testing-library/react";
import type { ProductSales, ShiftReport } from "../api";
import { json, renderPage } from "../testing";
import { SalesPage } from "./SalesPage";
import { ShiftReportPage } from "./ShiftReportPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const report: ShiftReport = {
  shift: {
    shiftId: "s1", storeId: "st", storeName: "Tienda Centro", registerId: "r1", cashierId: "c1", cashierName: "Ana", registerName: "Caja 1",
    zNumber: 7, expectedCash: 1100, countedCash: 1050, difference: -50, closedUtc: "2026-10-15T22:00:00Z",
  },
  openingFloat: 1000, openedUtc: "2026-10-15T12:00:00Z", salesCount: 1, salesTotal: 500, voidedCount: 1,
  payments: [{ method: "Cash", count: 1, amount: 300 }, { method: "Card", count: 1, amount: 200 }],
  cashMovements: [{ type: "Withdrawal", method: "Cash", count: 1, amount: 200 }],
};

/** `page` at `path` (matched by `route`), the API answering `routes` (a read-only member). */
function renderApp(page: ReactElement, path: string, route: string, routes: Record<string, unknown>) {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const hit = Object.keys(routes).find((r) => url.includes(r));
    return json(200, hit ? routes[hit] : []);
  }));
  renderPage(page, { path, route, context: { canEdit: false } });
}

it("TK-200: a closed shift opens as one printable Z page", async () => {
  renderApp(<ShiftReportPage />, "/shifts/s1", "shifts/:shiftId", { "/api/panel/shifts/s1": report });

  expect(await screen.findByText("Cierre de turno Z #7")).toBeTruthy();
  expect(screen.getByText("Caja 1 · Ana")).toBeTruthy();
  expect(screen.getByText("Salidas de efectivo · Efectivo")).toBeTruthy();
  expect(screen.getByText(/Faltaron RD\$ 50.00/)).toBeTruthy();
  expect(screen.getByRole("button", { name: "Imprimir o guardar PDF" })).toBeTruthy();
});

it("TK-201: without permission to see costs, top products show no profit", async () => {
  const products: ProductSales[] = [{ productId: "p1", description: "Bistec de res", quantity: 2, total: 500, profit: null, unit: "Pound" }];
  renderApp(<SalesPage />, "/sales", "sales", { "/api/panel/sales/top-products": products });

  expect(await screen.findByText("Bistec de res")).toBeTruthy();
  expect(screen.queryByText(/gana/)).toBeNull();
  expect(screen.queryByText(/ganancia/)).toBeNull();
  expect(screen.queryByText(/últimos 92 días/)).toBeNull();
});

it("R13: a range longer than 92 days says the top products cover only its last 92", async () => {
  const products: ProductSales[] = [{ productId: "p1", description: "Bistec de res", quantity: 2, total: 500, profit: null, unit: "Pound" }];
  renderApp(<SalesPage />, "/sales", "sales", { "/api/panel/sales/top-products": products });
  await screen.findByText("Bistec de res");

  fireEvent.click(screen.getByRole("button", { name: "Fechas" }));
  fireEvent.change(screen.getByLabelText("Desde"), { target: { value: "2025-01-01" } });
  expect(await screen.findByText("Con ITBIS · últimos 92 días")).toBeTruthy();
});
