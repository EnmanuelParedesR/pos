import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { App } from "../App";
import "../Panel"; // TK-278: the panel is lazy in App; loading it here keeps the first findBy from waiting on the import.
import type { AlertPreferences, Me, ReceivedEcf } from "../api";
import { json } from "../testing";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  window.location.hash = "";
});

const owner: Me = { email: "dueno@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "Owner", tenants: [{ tenantId: "t0", name: "Carnicería La Esquina", role: "Owner" }] };
const readOnly: Me = { ...owner, email: "contable@example.com", role: "ReadOnly", tenants: [{ tenantId: "t0", name: "Carnicería La Esquina", role: "ReadOnly" }] };

const prefs: AlertPreferences = {
  email: "dueno@example.com", dgiiRejected: true, certificateExpiring: true, queueStale: true, certificationProgress: true, unsubscribed: false, emailDelivers: true,
};

const received: ReceivedEcf = {
  id: 7, environment: "ecf", issuerRnc: "131245678", issuerName: "Distribuidora del Cibao", eNcf: "E310000000007", type: 31, issuedOn: "2026-10-14",
  total: 1180, itbis: 180, receivedUtc: "2026-10-15T12:00:00Z", approval: null, approvalReason: null, approvalUtc: null, approvalDeliveredUtc: null,
  approvalDgiiUtc: null, approvalMessage: null, acknowledgedUtc: null,
};

function renderApp(me: Me | null, path: string) {
  if (me) localStorage.setItem("abastalia.refreshToken", "saved");
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    if (url.endsWith("/api/me")) return json(200, me);
    if (url.endsWith("/api/notifications/unsubscribe")) return json(JSON.parse(String(init?.body)).token === "good" ? 204 : 404, { detail: "no" });
    if (url.endsWith("/api/notifications")) return json(200, method === "PUT" ? { ...prefs, ...JSON.parse(String(init?.body)) } : prefs);
    if (url.endsWith("/api/panel/fe/received")) return json(200, [received]);
    if (url.endsWith("/approval")) return json(200, { ...received, approval: "Rejected" });
    return json(200, []);
  });
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={[path]}>
      <App />
    </MemoryRouter>,
  );
  return fetch;
}

const bodies = (fetch: ReturnType<typeof renderApp>, method: string) =>
  fetch.mock.calls.filter(([, init]) => init?.method === method).map(([url, init]) => [String(url).replace(/^https?:\/\/[^/]+/, ""), init?.body]);

it("TK-222: a member turns an e-mail alert off", async () => {
  const fetch = renderApp(owner, "/notifications");

  fireEvent.click(await screen.findByLabelText(/Comprobantes rechazados por la DGII/));

  await vi.waitFor(() =>
    expect(bodies(fetch, "PUT")).toContainEqual(["/api/notifications", JSON.stringify({ dgiiRejected: false, certificateExpiring: true, queueStale: true, certificationProgress: true })]),
  );
  expect(screen.getByText("Le escribimos a dueno@example.com cuando algo de la tienda necesita su atención.")).toBeTruthy();
});

it("TK-222: the e-mail link unsubscribes without a session", async () => {
  window.location.hash = "#good";
  const fetch = renderApp(null, "/unsubscribe");

  expect(await screen.findByText("Listo: ya no le enviaremos avisos.")).toBeTruthy();
  expect(bodies(fetch, "POST")).toContainEqual(["/api/notifications/unsubscribe", JSON.stringify({ token: "good" })]);
});

it("TK-246/TK-250: the owner sees a received e-CF and can decide it; nothing is sent until the owner confirms", async () => {
  const fetch = renderApp(owner, "/received");

  fireEvent.click(await screen.findByText("Distribuidora del Cibao"));
  expect(screen.getByText("Acuse de recibo pendiente")).toBeTruthy();
  expect(screen.getByRole("button", { name: "Aceptar" })).toBeTruthy();
  expect(bodies(fetch, "POST").filter(([url]) => String(url).endsWith("/decision"))).toEqual([]);
});

it("TK-154: a read-only member sees received e-CF", async () => {
  renderApp(readOnly, "/received");

  fireEvent.click(await screen.findByText("Distribuidora del Cibao"));
  expect(screen.queryByRole("button", { name: "Aceptar" })).toBeNull();
});
