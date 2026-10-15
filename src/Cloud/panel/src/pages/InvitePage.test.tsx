import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import type { InvitePreview, Me } from "../api";
import { json } from "../testing";
import { InvitePage } from "./InvitePage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const preview: InvitePreview = { tenantName: "Carnicería La Esquina", email: "contable@example.com", role: "ReadOnly", expiresUtc: "2026-10-21T12:00:00Z" };
const me: Me = { email: "contable@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "ReadOnly", tenants: [] };
const tokens = { accessToken: "a", refreshToken: "r" };

function renderInvite(routes: (url: string, method: string) => Response | undefined) {
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    const path = url.replace(/^https?:\/\/[^/]+/, "");
    return routes(path, init?.method ?? "GET") ?? json(404, { code: "NotMocked", detail: path });
  });
  vi.stubGlobal("fetch", fetch);
  const onJoined = vi.fn();
  render(
    <MemoryRouter initialEntries={["/invite/tok"]}>
      <Routes>
        <Route path="/invite/:token" element={<InvitePage onJoined={onJoined} />} />
        <Route path="/" element={<p>Panel</p>} />
      </Routes>
    </MemoryRouter>,
  );
  return { fetch, onJoined };
}

it("TK-217: an invited person creates an account with the invited e-mail and lands in the panel", async () => {
  const { fetch, onJoined } = renderInvite((path, method) =>
    path === "/api/invites/tok" ? json(200, preview)
    : path === "/api/invites/tok/register" && method === "POST" ? json(204)
    : path === "/api/auth/login" ? json(200, tokens)
    : path === "/api/me" ? json(200, me)
    : undefined,
  );

  expect(await screen.findByText("Únase a Carnicería La Esquina")).toBeTruthy();
  expect((screen.getByLabelText("Correo") as HTMLInputElement).value).toBe("contable@example.com");
  fireEvent.change(screen.getByLabelText(/Contraseña/), { target: { value: "Carniceria2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Crear cuenta y unirme" }));

  expect(await screen.findByText("Panel")).toBeTruthy();
  expect(onJoined).toHaveBeenCalledWith(me);
  const register = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/invites/tok/register"))!;
  expect(register[1]?.body).toBe(JSON.stringify({ password: "Carniceria2026" }));
  const login = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/auth/login"))!;
  expect(login[1]?.body).toBe(JSON.stringify({ email: "contable@example.com", password: "Carniceria2026" }));
});

it("TK-217: someone already signed in joins with one tap and gets a token for the new business", async () => {
  localStorage.setItem("toki.refreshToken", "saved");
  const { fetch, onJoined } = renderInvite((path, method) =>
    path === "/api/invites/tok" ? json(200, preview)
    : path === "/api/invites/tok/accept" && method === "POST" ? json(204)
    : path === "/api/auth/refresh" ? json(200, tokens)
    : path === "/api/me" ? json(200, me)
    : undefined,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Unirme a Carnicería La Esquina" }));

  expect(await screen.findByText("Panel")).toBeTruthy();
  expect(onJoined).toHaveBeenCalledWith(me);
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/auth/refresh"))).toBe(true);
});

it("TK-217: a used, expired or revoked link says so", async () => {
  renderInvite((path) =>
    path === "/api/invites/tok" ? json(404, { code: "Invites.Invalid", detail: "La invitación no es válida, ya se usó o venció. Pida una nueva al dueño." }) : undefined,
  );

  expect(await screen.findByText("Invitación no válida")).toBeTruthy();
  expect(screen.getByText("La invitación no es válida, ya se usó o venció. Pida una nueva al dueño.")).toBeTruthy();
  expect(screen.queryByLabelText(/Contraseña/)).toBeNull();
});

it("TK-217: a wrong account gets the server's message", async () => {
  localStorage.setItem("toki.refreshToken", "saved");
  renderInvite((path) =>
    path === "/api/invites/tok" ? json(200, preview)
    : path === "/api/invites/tok/accept" ? json(403, { code: "Invites.WrongEmail", detail: "Esta invitación es para contable@example.com. Entre con esa cuenta." })
    : undefined,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Unirme a Carnicería La Esquina" }));

  expect(await screen.findByText("Esta invitación es para contable@example.com. Entre con esa cuenta.")).toBeTruthy();
  expect(screen.getByRole("button", { name: "Usar otra cuenta" })).toBeTruthy();
});
