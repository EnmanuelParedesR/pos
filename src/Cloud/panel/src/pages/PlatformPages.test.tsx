import { afterEach, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { App } from "../App";
import type { Me, MetricsOverview } from "../api";
import { json } from "../testing";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const overview: MetricsOverview = {
  today: "2026-10-15",
  tenants: 12,
  stores: 10,
  nodes: 9,
  nodesOnline: 7,
  activeToday: 6,
  activeLast7Days: 8,
  activeLast30Days: 9,
  salesLast30Days: 4200,
  amountLast30Days: 1_250_000,
  funnel: [
    { stage: "account", tenants: 12 },
    { stage: "store", tenants: 10 },
    { stage: "connected", tenants: 9 },
    { stage: "firstSale", tenants: 8 },
    { stage: "sevenDays", tenants: 6 },
  ],
  features: [{ feature: "onAccount", stores: 5, reporting: 8 }],
  health: { nodes: 9, staleNodes: 2, nodesWithErrors: 1, nodesWithBacklog: 0, failedEvents: 0, versions: [{ version: "1.4.0", nodes: 7 }] },
  churn: [{ tenantId: "t1", tenantName: "colmado@example.com", storeId: "s1", storeName: "Colmado La Fe", kind: "NoSales", lastSaleDay: "2026-10-01", lastSyncUtc: null }],
};

/** The whole app signed in as `me`, at `path`. */
function renderApp(me: Me, path: string) {
  localStorage.setItem("toki.refreshToken", "saved");
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) =>
      url.endsWith("/api/me")
        ? json(200, me)
        : url.includes("/api/platform/metrics/overview")
          ? json(200, overview)
          : url.includes("/api/panel/summary")
            ? json(200, { date: "2026-10-15", salesTotal: 0, salesCount: 0, averageTicket: 0, discountTotal: 0, itbisTotal: 0, returnsTotal: 0, netTotal: 0, voidedCount: 0, byHour: [], paymentMix: [], issues: [], lastEventUtc: null })
            : url.includes("/api/platform/metrics/daily")
              ? json(200, [{ day: "2026-10-15", activeStores: 6, sales: 140, amount: 41000 }])
              : json(200, []),
    ),
  );
  render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  );
}

const owner: Me = { email: "dueno@example.com", tenantId: "t0", tenantName: "dueno@example.com", isPlatformAdmin: false, role: "Owner", tenants: [{ tenantId: "t0", name: "dueno@example.com", role: "Owner" }] };

it("TK-189: an owner never sees the Plataforma area, not even by its address", async () => {
  renderApp(owner, "/platform");

  expect(await screen.findByText("Vendido hoy")).toBeTruthy(); // the owner's summary instead
  expect(screen.queryByRole("link", { name: /Plataforma/ })).toBeNull();
  expect(screen.queryByText("Embudo de activación")).toBeNull();
  const fetch = vi.mocked(globalThis.fetch);
  expect(fetch.mock.calls.some(([url]) => String(url).includes("/api/platform"))).toBe(false);
});

it("TK-189 / TK-190: Toki staff get the Plataforma area with the metrics dashboard", async () => {
  renderApp({ ...owner, email: "staff@toki.example", isPlatformAdmin: true }, "/platform");

  expect(await screen.findByText("Embudo de activación")).toBeTruthy();
  expect(screen.getAllByRole("link", { name: /Plataforma/ }).length).toBeGreaterThan(0);
  expect(screen.getByText("Tiendas activas hoy")).toBeTruthy();
  expect(screen.getByText("RD$ 1,250,000.00")).toBeTruthy();
  const funnel = screen.getByRole("region", { name: "Embudo de activación" });
  expect(within(funnel).getByText("7 días con ventas")).toBeTruthy();
  expect(within(funnel).getByText("50 %")).toBeTruthy(); // 6 of 12 accounts
  expect(screen.getByText("Colmado La Fe: dejó de vender")).toBeTruthy();
  expect(screen.getByText("5 de 8")).toBeTruthy(); // fiado
  expect(screen.queryByPlaceholderText(/Pregúntale a Toki/)).toBeNull(); // the owner's assistant is not for staff pages
});
