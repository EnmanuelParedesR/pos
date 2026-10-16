import { afterEach, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { App } from "../App";
import type { Me, ProductSales, ShiftReport } from "../api";
import { json } from "../testing";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const me: Me = { email: "contable@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "ReadOnly", tenants: [], canViewCosts: false };

const report: ShiftReport = {
  shift: {
    shiftId: "s1", storeId: "st", storeName: "Tienda Centro", registerId: "r1", cashierId: "c1", cashierName: "Ana", registerName: "Caja 1",
    zNumber: 7, expectedCash: 1100, countedCash: 1050, difference: -50, closedUtc: "2026-10-15T22:00:00Z",
  },
  openingFloat: 1000, openedUtc: "2026-10-15T12:00:00Z", salesCount: 1, salesTotal: 500, voidedCount: 1,
  payments: [{ method: "Cash", count: 1, amount: 300 }, { method: "Card", count: 1, amount: 200 }],
  cashMovements: [{ type: "Withdrawal", method: "Cash", count: 1, amount: 200 }],
};

function renderApp(path: string, routes: Record<string, unknown>) {
  localStorage.setItem("abastalia.refreshToken", "saved");
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    if (url.endsWith("/api/me")) return json(200, me);
    const hit = Object.keys(routes).find((r) => url.includes(r));
    return json(200, hit ? routes[hit] : []);
  }));
  render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  );
}

it("TK-200: a closed shift opens as one printable Z page", async () => {
  renderApp("/shifts/s1", { "/api/panel/shifts/s1": report });

  expect(await screen.findByText("Cierre de turno Z #7")).toBeTruthy();
  expect(screen.getByText("Caja 1 · Ana")).toBeTruthy();
  expect(screen.getByText("Salidas de efectivo · Efectivo")).toBeTruthy();
  expect(screen.getByText(/Faltaron RD\$ 50.00/)).toBeTruthy();
  expect(screen.getByRole("button", { name: "Imprimir o guardar PDF" })).toBeTruthy();
});

it("TK-201: without permission to see costs, top products show no profit", async () => {
  const products: ProductSales[] = [{ productId: "p1", description: "Bistec de res", quantity: 2, total: 500, profit: null, unit: "Pound" }];
  renderApp("/sales", { "/api/panel/sales/top-products": products });

  expect(await screen.findByText("Bistec de res")).toBeTruthy();
  expect(screen.queryByText(/gana/)).toBeNull();
  expect(screen.queryByText(/ganancia/)).toBeNull();
});
