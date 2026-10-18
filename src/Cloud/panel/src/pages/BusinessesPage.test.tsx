import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import type { Me } from "../api";
import { json, renderPage } from "../testing";
import { BusinessesPage } from "./BusinessesPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const me: Me = {
  email: "dueno@example.com",
  tenantId: "t0",
  tenantName: "Carnicería La Esquina",
  role: "Owner",
  tenants: [
    { tenantId: "t0", name: "Carnicería La Esquina", role: "Owner" },
    { tenantId: "t1", name: "Colmado La Fe", role: "ReadOnly" },
  ],
};

function render(onMe = vi.fn()) {
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    if (url.endsWith("/api/tenants") && method === "POST") return json(201, { tenantId: "t2", name: "Deli Don Pedro", role: "Owner" });
    if (url.endsWith("/api/me/tenant")) return json(204);
    if (url.endsWith("/api/auth/refresh")) return json(200, { accessToken: "a2", refreshToken: "r2" });
    if (url.endsWith("/api/me")) return json(200, { ...me, tenantId: "t2", tenantName: "Deli Don Pedro" });
    return json(404);
  });
  vi.stubGlobal("fetch", fetch);
  localStorage.setItem("abastalia.refreshToken", "saved");
  renderPage(<BusinessesPage me={me} onMe={onMe} />, { path: "/app/businesses", route: "app/businesses" });
  return { fetch, onMe };
}

const posts = (fetch: ReturnType<typeof vi.fn>) =>
  fetch.mock.calls.filter(([, init]) => (init as RequestInit | undefined)?.method === "POST").map(([url, init]) => [String(url).replace(/^.*\/api\//, "/api/"), (init as RequestInit).body]);

it("TK-379: an account opens another business of its own and the panel moves to it", async () => {
  const { fetch, onMe } = render();

  fireEvent.click(screen.getByRole("button", { name: "Agregar negocio" }));
  fireEvent.click(screen.getByRole("button", { name: "Abrir el negocio" }));
  expect(screen.getByRole("alert").textContent).toContain("Escriba el nombre del negocio.");

  fireEvent.change(screen.getByLabelText(/Nombre del negocio/), { target: { value: "  Deli Don Pedro " } });
  fireEvent.click(screen.getByRole("button", { name: "Abrir el negocio" }));

  await vi.waitFor(() => expect(onMe).toHaveBeenCalledWith(expect.objectContaining({ tenantId: "t2" })));
  expect(posts(fetch)).toContainEqual(["/api/tenants", JSON.stringify({ name: "Deli Don Pedro" })]);
  expect(posts(fetch).map(([url]) => url)).toContain("/api/auth/refresh"); // the token carries the new business
});

it("TK-379: the list marks the open business and opens another one", async () => {
  const { fetch } = render();

  const list = screen.getByRole("list", { name: "Negocios de esta cuenta" });
  const open = within(list).getByText("Carnicería La Esquina").closest("li")!;
  expect(within(open).getByText("Abierto")).toBeTruthy();
  const other = within(list).getByText("Colmado La Fe").closest("li")!;
  expect(within(other).getByText("Solo lectura")).toBeTruthy();

  fireEvent.click(within(other).getByRole("button", { name: "Abrir" }));
  await vi.waitFor(() => expect(posts(fetch)).toContainEqual(["/api/me/tenant", JSON.stringify({ tenantId: "t1" })]));
});
