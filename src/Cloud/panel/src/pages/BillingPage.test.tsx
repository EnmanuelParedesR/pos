import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { Billing, Checkout, PlatformBilling } from "../api";
import { json } from "../testing";
import { BillingPage, PlatformBillingCard, paddleScript } from "./BillingPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  delete window.Paddle;
});

const billing: Billing = {
  enabled: true,
  environment: "sandbox",
  plan: "Default",
  addOns: [],
  status: "None",
  interval: null,
  paidThroughUtc: null,
  nextBilledUtc: null,
  pastDueSinceUtc: null,
  manualUntilUtc: null,
  canManage: false,
  plans: [
    { code: "Default", name: "Estándar", monthly: { priceId: "pri_basic", interval: "Monthly", amount: "1000", currency: "USD" }, annual: null },
    { code: "MultiRegister", name: "Pro", monthly: { priceId: "pri_registers", interval: "Monthly", amount: "2500", currency: "USD" }, annual: null },
  ],
  addOnOptions: [{ code: "SupplierInvoiceReading", name: "Lectura de facturas", monthly: { priceId: "pri_inv", interval: "Monthly", amount: "500", currency: "USD" }, annual: null }],
};

const checkout: Checkout = {
  environment: "sandbox",
  clientToken: "test_token",
  items: [{ priceId: "pri_registers", quantity: 1 }],
  customerEmail: "dueno@example.com",
  customData: { tenantId: "t1" },
};

const page = () =>
  render(
    <MemoryRouter>
      <BillingPage />
    </MemoryRouter>,
  );

it("TK-249: without Paddle set up the owner sees the plan and how to ask Abastalia, and Paddle.js is not loaded", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(200, { ...billing, enabled: false, environment: null, plans: [], addOnOptions: [] })));
  page();

  expect(await screen.findByText("El pago en línea todavía no está disponible.")).toBeTruthy();
  expect(screen.getByText("Estándar")).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Elegir plan" })).toBeNull();
  expect(document.querySelector(`script[src="${paddleScript}"]`)).toBeNull();
});

it("TK-249: choosing a plan opens Paddle's checkout in the sandbox with the prices and the business", async () => {
  const paddle = { Environment: { set: vi.fn() }, Initialize: vi.fn(), Checkout: { open: vi.fn() } };
  window.Paddle = paddle;
  const fetch = vi.fn(async (_url: string, init?: RequestInit) => (init?.method === "POST" ? json(200, checkout) : json(200, billing)));
  vi.stubGlobal("fetch", fetch);
  page();

  const buttons = await screen.findAllByRole("button", { name: "Elegir plan" });
  fireEvent.change(screen.getByRole("spinbutton", { name: /Lectura de facturas/ }), { target: { value: "2" } }); // TK-258: add-ons by quantity
  expect(screen.getByText(/US\$25\.00 al mes/)).toBeTruthy(); // the price as Paddle has it
  expect(screen.getByText(/US\$5\.00 al mes/)).toBeTruthy();
  fireEvent.click(buttons[1]); // Pro

  await waitFor(() => expect(paddle.Checkout.open).toHaveBeenCalled());
  const [, init] = fetch.mock.calls.find(([, i]) => i?.method === "POST")!;
  expect(JSON.parse(String(init!.body))).toEqual({ plan: "MultiRegister", addOns: ["SupplierInvoiceReading", "SupplierInvoiceReading"], interval: "Monthly" });
  expect(paddle.Environment.set).toHaveBeenCalledWith("sandbox");
  expect(paddle.Initialize).toHaveBeenCalledWith(expect.objectContaining({ token: "test_token" }));
  expect(paddle.Checkout.open).toHaveBeenCalledWith(
    expect.objectContaining({ items: checkout.items, customer: { email: "dueno@example.com" }, customData: { tenantId: "t1" } }),
  );
});

it("R12 #10: an annual subscriber sees the plans at their annual price", async () => {
  const yearly = (code: string, amount: string) => ({ priceId: `pri_${code}_y`, interval: "Annual" as const, amount, currency: "USD" });
  vi.stubGlobal("fetch", vi.fn(async () => json(200, {
    ...billing, status: "Active", interval: "Annual", canManage: true,
    plans: billing.plans.map((p) => ({ ...p, annual: yearly(p.code, p.code === "Default" ? "10000" : "25000") })),
  })));
  page();
  expect(await screen.findByText(/US\$250\.00 al año/)).toBeTruthy();
  expect(screen.queryByText(/US\$25\.00 al mes/)).toBeNull();
});

it("TK-313: \"Sucursal adicional\" is offered only on Pro or more", async () => {
  const store = { code: "ExtraStore", name: "Sucursal adicional", monthly: { priceId: "pri_store", interval: "Monthly" as const, amount: "2500", currency: "USD" }, annual: null };
  const withStore = { ...billing, addOnOptions: [...billing.addOnOptions, store] };
  vi.stubGlobal("fetch", vi.fn(async () => json(200, withStore)));
  page();
  expect(await screen.findByRole("spinbutton", { name: /Lectura de facturas/ })).toBeTruthy();
  expect(screen.queryByRole("spinbutton", { name: /Sucursal adicional/ })).toBeNull(); // Estándar
  cleanup();
  vi.stubGlobal("fetch", vi.fn(async () => json(200, { ...withStore, plan: "MultiRegister" })));
  page();
  expect(await screen.findByRole("spinbutton", { name: /Sucursal adicional/ })).toBeTruthy();
});

it("TK-249: an overdue payment is explained and the portal button shows for a Paddle customer", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(200, { ...billing, status: "PastDue", canManage: true, plan: "MultiRegister", paidThroughUtc: "2026-11-15T15:00:00Z" })));
  window.Paddle = { Environment: { set: vi.fn() }, Initialize: vi.fn(), Checkout: { open: vi.fn() } };
  page();

  expect(await screen.findByText("No pudimos cobrar su tarjeta.")).toBeTruthy();
  expect(screen.getByText(/siempre puede vender/)).toBeTruthy();
  expect(screen.getByRole("button", { name: "Administrar pago" })).toBeTruthy();
  expect(screen.getByRole("button", { name: "Su plan" }).hasAttribute("disabled")).toBe(true);
});

it("TK-249: staff set a paid-through deal with its reason", async () => {
  const view: PlatformBilling = {
    subscription: {
      status: "None", interval: null, paddleSubscriptionId: null, paddleCustomerId: null, paidThroughUtc: null, nextBilledUtc: null,
      pastDueSinceUtc: null, manualUntilUtc: null, manualNote: null, updatedUtc: null,
    },
    events: [],
  };
  const fetch = vi.fn(async (_url: string, init?: RequestInit) => (init?.method === "PUT" ? json(204) : json(200, view)));
  vi.stubGlobal("fetch", fetch);
  render(<PlatformBillingCard tenantId="t1" />);

  fireEvent.change(await screen.findByLabelText("Pagado hasta (acuerdo)"), { target: { value: "2026-12-31" } });
  const save = screen.getByRole("button", { name: "Guardar acuerdo" });
  expect(save.hasAttribute("disabled")).toBe(true); // the reason is required
  fireEvent.change(screen.getByLabelText("Motivo"), { target: { value: "Feria: un mes gratis" } });
  fireEvent.click(save);

  await waitFor(() => expect(fetch.mock.calls.some(([, i]) => i?.method === "PUT")).toBe(true));
  const [url, init] = fetch.mock.calls.find(([, i]) => i?.method === "PUT")!;
  expect(url).toMatch(/\/api\/platform\/tenants\/t1\/billing$/);
  expect(JSON.parse(String(init!.body))).toEqual({ manualUntilUtc: "2026-12-31T23:59:59Z", note: "Feria: un mes gratis" });
});

it("TK-279: staff block a business with its reason, and unblock it", async () => {
  let blocked: string | null = null;
  const view = (): PlatformBilling => ({
    subscription: {
      status: "PastDue", interval: null, paddleSubscriptionId: null, paddleCustomerId: null, paidThroughUtc: null, nextBilledUtc: null,
      pastDueSinceUtc: null, manualUntilUtc: null, manualNote: null, updatedUtc: null, blockedUtc: blocked, blockNote: blocked ? "Sin pago" : null,
    },
    events: [],
  });
  const fetch = vi.fn(async (_url: string, init?: RequestInit) => {
    if (init?.method !== "PUT") return json(200, view());
    blocked = JSON.parse(String(init.body)).blocked ? "2026-10-16T00:00:00Z" : null;
    return json(204);
  });
  vi.stubGlobal("fetch", fetch);
  render(<PlatformBillingCard tenantId="t1" />);

  const button = await screen.findByRole("button", { name: "Bloquear negocio" });
  expect(button.hasAttribute("disabled")).toBe(true); // the reason is required
  fireEvent.change(screen.getByLabelText("Motivo del bloqueo o desbloqueo"), { target: { value: "Sin pago" } });
  fireEvent.click(button);
  await screen.findByText(/Bloqueado desde el/);
  const [url, init] = fetch.mock.calls.find(([, i]) => i?.method === "PUT")!;
  expect(url).toMatch(/\/api\/platform\/tenants\/t1\/block$/);
  expect(JSON.parse(String(init!.body))).toEqual({ blocked: true, reason: "Sin pago" });

  // TK-287: no paid time, so unblocking alone leaves the stores blocked: the console says so before and after.
  expect(screen.getByText("Este negocio no tiene tiempo pagado.")).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Motivo del bloqueo o desbloqueo"), { target: { value: "Pagó por transferencia" } });
  fireEvent.click(screen.getByRole("button", { name: "Desbloquear negocio" }));
  await screen.findByRole("button", { name: "Bloquear negocio" });
  expect(screen.getByText("Desbloqueado, pero sus tiendas siguen bloqueadas.")).toBeTruthy();
});

it("TK-287: unblocking a business with paid time shows no warning", async () => {
  let blocked: string | null = "2026-10-16T00:00:00Z";
  const view = (): PlatformBilling => ({
    subscription: {
      status: "Active", interval: null, paddleSubscriptionId: null, paddleCustomerId: null, paidThroughUtc: "2999-01-01T00:00:00Z", nextBilledUtc: null,
      pastDueSinceUtc: null, manualUntilUtc: null, manualNote: null, updatedUtc: null, blockedUtc: blocked, blockNote: null,
    },
    events: [],
  });
  vi.stubGlobal("fetch", vi.fn(async (_url: string, init?: RequestInit) => {
    if (init?.method !== "PUT") return json(200, view());
    blocked = null;
    return json(204);
  }));
  render(<PlatformBillingCard tenantId="t1" />);

  fireEvent.change(await screen.findByLabelText("Motivo del bloqueo o desbloqueo"), { target: { value: "Pagó" } });
  fireEvent.click(screen.getByRole("button", { name: "Desbloquear negocio" }));
  await screen.findByRole("button", { name: "Bloquear negocio" });
  expect(screen.queryByText(/tiempo pagado|siguen bloqueadas/)).toBeNull();
});
