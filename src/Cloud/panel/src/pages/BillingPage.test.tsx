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

it("TK-249: without Paddle set up the owner sees the plan and how to ask Toki, and Paddle.js is not loaded", async () => {
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
