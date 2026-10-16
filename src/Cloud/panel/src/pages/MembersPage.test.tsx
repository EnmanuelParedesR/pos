import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { App } from "../App";
import type { Me, Members } from "../api";
import { json } from "../testing";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const owner: Me = {
  email: "dueno@example.com",
  tenantId: "t0",
  tenantName: "Carnicería La Esquina",
  role: "Owner",
  tenants: [{ tenantId: "t0", name: "Carnicería La Esquina", role: "Owner" }],
};

const members: Members = {
  members: [
    { userId: "u1", email: "dueno@example.com", role: "Owner", joinedUtc: "2026-10-01T12:00:00Z" },
    { userId: "u2", email: "contable@example.com", role: "ReadOnly", joinedUtc: "2026-10-10T12:00:00Z" },
  ],
  invites: [{ id: "i1", email: "hijo@example.com", role: "ReadOnly", invitedBy: "dueno@example.com", createdUtc: "2026-10-14T12:00:00Z", expiresUtc: "2026-10-21T12:00:00Z" }],
};

const summary = { date: "2026-10-15", salesTotal: 0, salesCount: 0, averageTicket: 0, discountTotal: 0, itbisTotal: 0, returnsTotal: 0, netTotal: 0, voidedCount: 0, byHour: [], paymentMix: [], issues: [], lastEventUtc: null };

/** The whole app signed in as `me`, at `path`; returns the fetch mock. */
function renderApp(me: Me, path: string) {
  localStorage.setItem("toki.refreshToken", "saved");
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    if (url.endsWith("/api/me")) return json(200, me);
    if (url.endsWith("/api/members") && method === "GET") return json(200, members);
    if (url.endsWith("/api/members/invites") && method === "POST")
      return json(200, { invite: { ...members.invites[0], id: "i2", email: "nuevo@example.com" }, link: "https://panel.toki.do/invite#abc" });
    if (url.includes("/api/members/") && (method === "PUT" || method === "DELETE")) return json(204);
    if (url.includes("/api/panel/summary")) return json(200, summary);
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

const calls = (fetch: ReturnType<typeof renderApp>, method: string) =>
  fetch.mock.calls.filter(([, init]) => init?.method === method).map(([url, init]) => [String(url).replace(/^https?:\/\/[^/]+/, ""), init?.body]);

it("TK-217: an owner sees the team, invites someone and gets the link to copy", async () => {
  const fetch = renderApp(owner, "/members");

  expect(await screen.findByText("contable@example.com")).toBeTruthy();
  expect(screen.getByText("dueno@example.com (usted)")).toBeTruthy();
  expect(screen.getByText("hijo@example.com")).toBeTruthy();

  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "nuevo@example.com" } });
  fireEvent.click(screen.getByRole("button", { name: "Dueño" }));
  fireEvent.click(screen.getByRole("button", { name: /Invitar/ }));

  expect(await screen.findByText("Invitación lista para nuevo@example.com.")).toBeTruthy();
  expect(screen.getByText("https://panel.toki.do/invite#abc")).toBeTruthy();
  expect(calls(fetch, "POST")).toContainEqual(["/api/members/invites", JSON.stringify({ email: "nuevo@example.com", role: "Owner" })]);
});

it("TK-217: an owner changes a role and removes a member after confirming", async () => {
  const fetch = renderApp(owner, "/members");

  fireEvent.change(await screen.findByLabelText("Rol de contable@example.com"), { target: { value: "Owner" } });
  const row = screen.getByText("contable@example.com").closest("li")!;
  fireEvent.click(within(row).getByRole("button", { name: "Quitar" }));
  expect(within(row).getByText("Ya no podrá entrar a este panel.")).toBeTruthy();
  fireEvent.click(within(row).getByRole("button", { name: "Sí, quitar" }));

  await vi.waitFor(() => expect(calls(fetch, "DELETE")).toContainEqual(["/api/members/u2", undefined]));
  expect(calls(fetch, "PUT")).toContainEqual(["/api/members/u2/role", JSON.stringify({ role: "Owner" })]);
});

it("TK-217: a read-only member has no Equipo section, not even by its address", async () => {
  const fetch = renderApp({ ...owner, email: "contable@example.com", role: "ReadOnly", tenants: [{ tenantId: "t0", name: "Carnicería La Esquina", role: "ReadOnly" }] }, "/members");

  expect(await screen.findByText("Vendido hoy")).toBeTruthy(); // the summary instead
  expect(screen.queryByRole("link", { name: /Equipo/ })).toBeNull();
  expect(screen.getAllByText("Solo lectura").length).toBeGreaterThan(0);
  expect(fetch.mock.calls.some(([url]) => String(url).includes("/api/members"))).toBe(false);
});

it("TK-217: a member of two businesses can switch between them", async () => {
  renderApp({ ...owner, tenants: [...owner.tenants, { tenantId: "t1", name: "Colmado La Fe", role: "ReadOnly" }] }, "/");

  const select = await screen.findByLabelText("Negocio");
  expect(within(select).getByText("Colmado La Fe")).toBeTruthy();
});

it("TK-201: an owner lets a read-only member see cost and profit", async () => {
  const fetch = renderApp(owner, "/members");

  const row = (await screen.findByText("contable@example.com")).closest("li")!;
  fireEvent.click(within(row).getByLabelText("Ve costos y ganancias"));

  await vi.waitFor(() => expect(calls(fetch, "PUT")).toContainEqual(["/api/members/u2/costs", JSON.stringify({ canViewCosts: true })]));
  expect(within(screen.getByText("dueno@example.com (usted)").closest("li")!).queryByLabelText("Ve costos y ganancias")).toBeNull(); // Owners always see them
});
