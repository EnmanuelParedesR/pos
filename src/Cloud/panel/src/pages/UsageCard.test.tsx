import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { Usage } from "../api";
import { json } from "../testing";
import { PlatformUsageCard, UsageCard } from "./UsageCard";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const usage: Usage = {
  plan: "Default",
  addOns: [],
  resetsOn: "2026-11-01",
  meters: [
    { meter: "SupplierInvoices", used: 19, limit: 20 },
    { meter: "Emails", used: 4, limit: 300 },
  ],
};

it("TK-248: the owner sees this month's use of each service against the plan, and the add-on hint", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(200, usage)));
  render(<UsageCard />);

  expect(await screen.findByText("19 de 20")).toBeTruthy();
  expect(screen.getByText("Facturas leídas con IA")).toBeTruthy();
  expect(screen.getByText(/Plan Estándar · se renueva el/)).toBeTruthy();
  expect(screen.getByText(/Agregue la lectura de facturas con IA/)).toBeTruthy();
});

it("TK-248: staff change the plan and add-ons of a business from its page", async () => {
  const fetch = vi.fn(async (_url: string, init?: RequestInit) =>
    init?.method === "PUT" ? json(204) : json(200, { usage, plans: ["Default", "MultiStore"], addOns: ["ExtraRegister", "SupplierInvoiceReading"] }),
  );
  vi.stubGlobal("fetch", fetch);
  render(<PlatformUsageCard tenantId="t1" />);

  fireEvent.change(await screen.findByRole("combobox"), { target: { value: "MultiStore" } });
  fireEvent.change(screen.getByRole("spinbutton", { name: /Lectura de facturas con IA/ }), { target: { value: "1" } });
  fireEvent.change(screen.getByRole("spinbutton", { name: /Caja adicional/ }), { target: { value: "2" } }); // TK-258: by quantity
  fireEvent.click(screen.getByRole("button", { name: "Guardar plan" }));

  await waitFor(() => expect(fetch.mock.calls.some(([, init]) => init?.method === "PUT")).toBe(true));
  const [url, init] = fetch.mock.calls.find(([, i]) => i?.method === "PUT")!;
  expect(url).toMatch(/\/api\/platform\/tenants\/t1\/plan$/);
  expect(JSON.parse(String(init!.body))).toEqual({ plan: "MultiStore", addOns: ["ExtraRegister", "ExtraRegister", "SupplierInvoiceReading"] });
});
