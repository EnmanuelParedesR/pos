import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { Billing, Checkout, PlatformReferrals, Referrals } from "../api";
import { json } from "../testing";
import { BillingPage } from "./BillingPage";
import { PlatformReferralsCard, ReferralCard } from "./ReferralCards";
import { App } from "../App";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  delete window.Paddle;
  window.history.replaceState(null, "", "/");
});

const referrals: Referrals = {
  enabled: true,
  code: "ABCD2345",
  pending: 1,
  applied: 2,
  reward: { type: "percentage", amount: "10", currency: "USD" },
  nextDiscount: { type: "percentage", amount: "10", currency: "USD", fromUtc: "2026-11-15T15:00:00Z" },
};

it("TK-255: the owner sees the link to share, the referrals and the next discount", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(200, referrals)));
  render(<ReferralCard />);

  expect(((await screen.findByLabelText("Su enlace")) as HTMLInputElement).value).toBe(`${window.location.origin}/precios?ref=ABCD2345`);
  expect(screen.getByText(/le descontamos 10 % en su próximo pago/)).toBeTruthy();
  expect(screen.getByText("Pagaron y esperan su descuento").nextSibling?.textContent).toBe("1");
  expect(screen.getByText(/10 % en su pago del/)).toBeTruthy();
});

it("TK-255: nothing shows while referrals are off", async () => {
  const fetch = vi.fn(async () => json(200, { ...referrals, enabled: false, code: null }));
  vi.stubGlobal("fetch", fetch);
  const { container } = render(<ReferralCard />);
  await waitFor(() => expect(fetch).toHaveBeenCalled());
  expect(container.textContent).toBe("");
});

it("TK-255: /precios?ref= keeps a valid code and the first checkout sends it", async () => {
  window.history.replaceState(null, "", "/precios?ref=abcd2345");
  const lookup = vi.fn(async (url: string) => (url.endsWith("/api/public/referrals/ABCD2345") ? json(200, { valid: true }) : json(404)));
  vi.stubGlobal("fetch", lookup);
  render(
    <MemoryRouter initialEntries={["/precios?ref=abcd2345"]}>
      <App />
    </MemoryRouter>,
  );
  expect(await screen.findByText(/Viene recomendado por un cliente de Toki/)).toBeTruthy();
  expect(localStorage.getItem("toki.ref")).toBe("ABCD2345");
  cleanup();

  const billing: Billing = {
    enabled: true, environment: "sandbox", plan: "Default", addOns: [], status: "None", interval: null, paidThroughUtc: null, nextBilledUtc: null,
    pastDueSinceUtc: null, manualUntilUtc: null, canManage: false, addOnOptions: [],
    plans: [{ code: "MultiRegister", name: "Varias cajas", monthly: { priceId: "pri_r", interval: "Monthly", amount: "2500", currency: "USD" }, annual: null }],
  };
  const checkout: Checkout = { environment: "sandbox", clientToken: "t", items: [{ priceId: "pri_r", quantity: 1 }], customerEmail: null, customData: { tenantId: "t1" } };
  window.Paddle = { Environment: { set: vi.fn() }, Initialize: vi.fn(), Checkout: { open: vi.fn() } };
  const fetch = vi.fn(async (url: string, init?: RequestInit) =>
    init?.method === "POST" ? json(200, checkout) : url.endsWith("/api/billing/referrals") ? json(200, { ...referrals, enabled: false }) : json(200, billing));
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter>
      <BillingPage />
    </MemoryRouter>,
  );
  fireEvent.click(await screen.findByRole("button", { name: "Elegir plan" }));
  await waitFor(() => expect(fetch.mock.calls.some(([, i]) => i?.method === "POST")).toBe(true));
  const [, init] = fetch.mock.calls.find(([, i]) => i?.method === "POST")!;
  expect(JSON.parse(String(init!.body))).toEqual({ plan: "MultiRegister", addOns: [], interval: "Monthly", referralCode: "ABCD2345" });
});

it("TK-255: staff revert a referral with a reason", async () => {
  const view: PlatformReferrals = {
    code: "ABCD2345",
    referredByCode: null,
    referrals: [
      {
        id: "r1", referrerTenantId: "t1", referrerName: "Carnicería La Esquina", referredTenantId: "t2", referredName: "Colmado Don Pedro", status: "Applied",
        reason: null, discountType: "percentage", discountAmount: "10", currency: "USD", createdUtc: "2026-10-15T15:00:00Z", appliedUtc: "2026-10-15T15:00:00Z",
        discountFromUtc: "2026-11-15T15:00:00Z", revertedUtc: null, note: null,
      },
    ],
  };
  const fetch = vi.fn(async (_url: string, init?: RequestInit) => (init?.method === "POST" ? json(204) : json(200, view)));
  vi.stubGlobal("fetch", fetch);
  render(<PlatformReferralsCard tenantId="t1" />);

  expect(await screen.findByText(/Recomendó a Colmado Don Pedro/)).toBeTruthy();
  const revert = screen.getByRole("button", { name: "Revertir" });
  expect(revert.hasAttribute("disabled")).toBe(true); // the reason is required
  fireEvent.change(screen.getByLabelText("Motivo del ajuste"), { target: { value: "Reembolso acordado" } });
  fireEvent.click(revert);

  await waitFor(() => expect(fetch.mock.calls.some(([, i]) => i?.method === "POST")).toBe(true));
  const [url, init] = fetch.mock.calls.find(([, i]) => i?.method === "POST")!;
  expect(url).toMatch(/\/api\/platform\/tenants\/t1\/referrals\/r1$/);
  expect(JSON.parse(String(init!.body))).toEqual({ action: "Revert", note: "Reembolso acordado" });
});
