import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { App } from "../App";
import "../Panel"; // TK-278: the panel is lazy in App; loading it here keeps the first findBy from waiting on the import.
import type { Me, MetricsOverview, PlatformAccompaniment, PlatformWatch } from "../api";
import { json } from "../testing";
import { fromStoreInput, toStoreInput } from "./PlatformPages";

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
    { stage: "certified", tenants: 1 },
  ],
  features: [{ feature: "onAccount", stores: 5, reporting: 8 }],
  health: { nodes: 9, staleNodes: 2, nodesWithErrors: 1, nodesWithBacklog: 0, failedEvents: 0, versions: [{ version: "1.4.0", nodes: 7 }] },
  churn: [{ tenantId: "t1", tenantName: "colmado@example.com", storeId: "s1", storeName: "Colmado La Fe", kind: "NoSales", lastSaleDay: "2026-10-01", lastSyncUtc: null }],
  certification: [
    { tenantId: "t2", tenantName: "parado@example.com", storeId: "s2", storeName: "Colmado Parado", currentStep: 3, totalSteps: 14, stepsDone: 3, certified: false, stepSinceUtc: "2026-09-20T12:00:00Z", daysInStep: 25, stuck: true },
    { tenantId: "t3", tenantName: "listo@example.com", storeId: "s3", storeName: "Súper Listo", currentStep: 14, totalSteps: 14, stepsDone: 14, certified: true, stepSinceUtc: "2026-10-10T12:00:00Z", daysInStep: 5, stuck: false },
  ],
};

/** The whole app signed in as `me`, at `path`. */
function renderApp(me: Me, path: string) {
  localStorage.setItem("abastalia.refreshToken", "saved");
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

it("TK-189 / TK-190: Abastalia staff get the Plataforma area with the metrics dashboard", async () => {
  renderApp({ ...owner, email: "staff@abastalia.example", isPlatformAdmin: true }, "/platform");

  expect(await screen.findByText("Embudo de activación")).toBeTruthy();
  expect(screen.getAllByRole("link", { name: /Plataforma/ }).length).toBeGreaterThan(0);
  expect(screen.getByText("Tiendas activas hoy")).toBeTruthy();
  expect(screen.getByText("RD$ 1,250,000.00")).toBeTruthy();
  const funnel = screen.getByRole("region", { name: "Embudo de activación" });
  expect(within(funnel).getByText("7 días con ventas")).toBeTruthy();
  expect(within(funnel).getByText("50 %")).toBeTruthy(); // 6 of 12 accounts
  expect(screen.getByText("Colmado La Fe: dejó de vender")).toBeTruthy();
  expect(screen.getByText("5 de 8")).toBeTruthy(); // fiado
  // TK-225: the certification, stuck stores first.
  const certification = screen.getByText("Colmado Parado").closest("tr")!;
  expect(within(certification).getByText("4 de 14 (3 listos)")).toBeTruthy();
  expect(within(certification).getByText("Detenida")).toBeTruthy();
  expect(within(screen.getByText("Súper Listo").closest("tr")!).getByText("Certificada")).toBeTruthy();
  expect(within(funnel).getByText("Certificado DGII")).toBeTruthy();
  expect(screen.queryByPlaceholderText(/Pregúntale a Talia/)).toBeNull(); // the owner's assistant is not for staff pages
});

it("TK-234: staff list the accompaniment requests and schedule session 1 in store time", async () => {
  localStorage.setItem("abastalia.refreshToken", "saved");
  const request: PlatformAccompaniment = {
    id: "a1", tenantId: "t2", tenantName: "colmado@example.com", storeId: "s2", storeName: "Colmado Acompañado", contactName: "Ana Pérez", phone: "809-555-1234",
    preferredTimes: "Mañanas", status: "New", session1Utc: null, session2Utc: null, notes: null, createdUtc: "2026-10-15T15:00:00Z", changedUtc: "2026-10-15T15:00:00Z",
  };
  const fetch = vi.fn(async (url: string, init?: RequestInit) =>
    url.endsWith("/api/me")
      ? json(200, { ...owner, email: "staff@abastalia.example", isPlatformAdmin: true })
      : url.endsWith("/api/platform/accompaniment") && (init?.method ?? "GET") === "GET"
        ? json(200, [request])
        : url.includes("/api/platform/accompaniment/a1")
          ? json(200, { ...request, status: "Scheduled", session1Utc: "2026-10-20T14:00:00.000Z" })
          : json(200, []),
  );
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={["/app/platform/accompaniment"]}>
      <App />
    </MemoryRouter>,
  );

  const row = (await screen.findByText("Colmado Acompañado")).closest("tr")!;
  expect(within(row).getByText("Nueva")).toBeTruthy();
  expect(within(row).getByText("809-555-1234")).toBeTruthy();
  expect(within(row).getByText("Sin agendar")).toBeTruthy();
  expect(screen.getByRole("tab", { name: "Acompañamiento" }).getAttribute("aria-selected")).toBe("true");

  fireEvent.click(within(row).getByRole("button", { name: "Editar" }));
  const form = screen.getByRole("form", { name: "Editar la solicitud de Colmado Acompañado" });
  fireEvent.change(within(form).getByLabelText("Estado"), { target: { value: "Scheduled" } });
  fireEvent.change(within(form).getByLabelText("Cita 1 (hora de la tienda)"), { target: { value: "2026-10-20T10:00" } });
  fireEvent.click(within(form).getByRole("button", { name: "Guardar" }));

  await waitFor(() => expect(fetch.mock.calls.some(([, init]) => init?.method === "PUT")).toBe(true));
  const [url, init] = fetch.mock.calls.find(([, init]) => init?.method === "PUT")!;
  expect(String(url)).toContain("/api/platform/accompaniment/a1");
  expect(JSON.parse(String(init!.body))).toEqual({ status: "Scheduled", session1Utc: "2026-10-20T14:00:00.000Z", session2Utc: null, notes: "" });
});

it("TK-234: store time in the date box round-trips to UTC", () => {
  expect(fromStoreInput("2026-10-20T10:00")).toBe("2026-10-20T14:00:00.000Z");
  expect(toStoreInput("2026-10-20T14:00:00Z")).toBe("2026-10-20T10:00");
  expect(fromStoreInput("")).toBeNull();
});

it("TK-311: staff see the platform health with thresholds highlighted, for 24 h or 7 days", async () => {
  localStorage.setItem("abastalia.refreshToken", "saved");
  const watch: PlatformWatch = {
    days: 1,
    sinceUtc: "2026-10-14T15:00:00Z",
    thresholds: { perIpRejections15Min: 20, storesBehindIp: 3, silentStoreHours: 6, queueStaleHours: 24, quotaNearPercent: 80 },
    reasons: [
      { reason: "Gate.PerIpRate", detail: "-", count: 42, retryAfter: 42 },
      { reason: "RateLimit", detail: "panel-user", count: 3, retryAfter: 3 },
      { reason: "Http5xx", detail: "/api/panel/summary", count: 2, retryAfter: 0 },
    ],
    ips: [{ network: "190.80.12.0/24", ipKey: "a1b2c3d4e5f60718", count: 42, peak15Min: 30, stores: 4, reasons: ["Gate.PerIpRate"] }],
    quotas: [{ tenantId: "t2", tenantName: "colmado@example.com", meter: "ReceiverSignatures", used: 3100, limit: 3000 }],
    silentStores: [{ tenantId: "t2", tenantName: "colmado@example.com", storeId: "s2", storeName: "Colmado Callado", lastHeartbeatUtc: "2026-10-15T07:00:00Z", queueHours: null }],
    ecfQueues: [{ tenantId: "t3", tenantName: "cola@example.com", storeId: "s3", storeName: "Colmado con Cola", lastHeartbeatUtc: "2026-10-15T14:50:00Z", queueHours: 80 }],
  };
  const fetch = vi.fn(async (url: string) =>
    url.endsWith("/api/me") ? json(200, { ...owner, email: "staff@abastalia.example", isPlatformAdmin: true }) : url.includes("/api/platform/watch") ? json(200, watch) : json(200, []),
  );
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={["/app/platform/health"]}>
      <App />
    </MemoryRouter>,
  );

  const ip = (await screen.findByText("190.80.12.0/24")).closest("tr")!;
  expect(within(ip).getByText("30 · alerta")).toBeTruthy(); // over 20 in 15 minutes
  expect(within(ip).getByText("4 · alerta")).toBeTruthy(); // 3 or more stores behind it
  expect(within(ip).queryByText(/a1b2c3d4e5f60718/)).toBeNull(); // only the start of the key
  expect(screen.getByRole("tab", { name: "Salud" }).getAttribute("aria-selected")).toBe("true");
  expect(screen.getByText("Panel, por usuario")).toBeTruthy();
  expect(screen.getByText("/api/panel/summary")).toBeTruthy();
  expect(within(screen.getByText("colmado@example.com", { selector: "a" }).closest("tr")!).getByText("Pasó el uso justo")).toBeTruthy();
  expect(screen.getByText("Colmado Callado")).toBeTruthy();
  expect(within(screen.getByText("Colmado con Cola").closest("li")!).getByText("Pasó las 72 h")).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "Últimos 7 días" }));
  await waitFor(() => expect(fetch.mock.calls.some(([url]) => String(url).includes("/api/platform/watch?days=7"))).toBe(true));
});
