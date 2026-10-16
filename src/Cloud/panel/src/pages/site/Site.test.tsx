import { afterEach, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router";
import { App } from "../../App";
import type { Me } from "../../api";
import { json } from "../../testing";
import { preparation, sitePages } from "./SitePages";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const owner: Me = { email: "dueno@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "Owner", tenants: [] };

function Where() {
  const { pathname } = useLocation();
  return <output data-testid="where">{pathname}</output>;
}

function renderAt(path: string, me?: Me) {
  if (me) localStorage.setItem("toki.refreshToken", "saved");
  const fetch = vi.fn(async (url: string) => (url.endsWith("/api/me") ? json(200, me) : url.endsWith("/api/auth/refresh") ? json(200, { accessToken: "a", refreshToken: "r" }) : json(200, [])));
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={[path]}>
      <App />
      <Where />
    </MemoryRouter>,
  );
  return fetch;
}

it.each(sitePages.map((p) => [p.path, p.title]))("TK-228: %s is public: it renders without a session, never calls the API and sets its title", (path, title) => {
  localStorage.setItem("toki.refreshToken", "saved"); // even signed in, the site never asks for the account
  const fetch = renderAt(path);

  expect(screen.getByRole("heading", { level: 1 })).toBeTruthy();
  expect(screen.getByRole("navigation", { name: "Principal" })).toBeTruthy();
  expect(document.title).toBe(title);
  expect(fetch).not.toHaveBeenCalled();
});

it("TK-228: the prices stay as placeholders until the owner confirms them", () => {
  renderAt("/precios");
  expect(screen.getAllByText("Precio por confirmar").length).toBe(3);
});

it("TK-228: an unknown address shows the site's 404", () => {
  renderAt("/no-existe");
  expect(screen.getByRole("heading", { name: "No encontramos esa página." })).toBeTruthy();
});

it("TK-228: /app asks for the account when there is no session", () => {
  renderAt("/app");
  expect(screen.getByRole("heading", { name: "Bienvenido de nuevo" })).toBeTruthy();
});

it("TK-228: /app with a session opens the panel", async () => {
  renderAt("/app", owner);
  expect(await screen.findAllByText("Carnicería La Esquina")).toBeTruthy();
  expect(screen.getAllByRole("link", { name: "Resumen" })[0].getAttribute("href")).toBe("/app");
});

it("TK-228: old panel links forward to /app, keeping the rest of the address", () => {
  renderAt("/shifts/42?x=1");
  expect(screen.getByTestId("where").textContent).toBe("/app/shifts/42");
  expect(screen.getByRole("heading", { name: "Bienvenido de nuevo" })).toBeTruthy();
});

it("TK-235: /certificacion lists everything the owner prepares before the first session", () => {
  renderAt("/certificacion");
  const list = document.getElementById("preparacion")!;
  for (const item of preparation) expect(list.textContent).toContain(item);
  expect(list.textContent).toContain(".p12 o .pfx");
  expect(list.textContent).toContain("Toki nunca te pide esa clave");
});
