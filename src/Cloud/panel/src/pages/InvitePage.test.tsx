import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
import { signOut, type InvitePreview, type Me } from "../api";
import { json } from "../testing";
import { InvitePage } from "./InvitePage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
  signOut(); // the access token lives in module memory
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
  render(
    <MemoryRouter initialEntries={["/invite#tok"]}>
      <Routes>
        <Route path="/invite" element={<InvitePage />} />
        <Route path="/app" element={<p>Panel</p>} />
      </Routes>
    </MemoryRouter>,
  );
  return { fetch };
}

it("TK-217 / R8 #12: an invited person (token after #) creates an account with the invited e-mail and lands in the panel", async () => {
  const { fetch } = renderInvite((path, method) =>
    path === "/api/invites/preview" ? json(200, preview)
    : path === "/api/invites/register" && method === "POST" ? json(204)
    : path === "/api/auth/login" ? json(200, tokens)
    : path === "/api/me" ? json(200, me)
    : undefined,
  );

  expect(await screen.findByText("Únase al equipo de Carnicería La Esquina")).toBeTruthy();
  expect(screen.getByText("contable@example.com")).toBeTruthy();
  expect(screen.getByText("Solo lectura")).toBeTruthy(); // the role badge
  expect(screen.getByText(/El enlace vence el .* y sirve una sola vez/)).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Cree una contraseña"), { target: { value: "Carniceria2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Unirme al equipo" }));

  expect(await screen.findByText("Panel")).toBeTruthy();
  // R8 #12: the token is only ever in a request body.
  expect(fetch.mock.calls.every(([url]) => !String(url).includes("tok"))).toBe(true);
  const previewCall = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/invites/preview"))!;
  expect(previewCall[1]?.body).toBe(JSON.stringify({ token: "tok" }));
  const register = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/invites/register"))!;
  expect(register[1]?.body).toBe(JSON.stringify({ token: "tok", password: "Carniceria2026" }));
  const login = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/auth/login"))!;
  expect(login[1]?.body).toBe(JSON.stringify({ email: "contable@example.com", password: "Carniceria2026" }));
});

it("TK-217: someone already signed in joins with one tap and gets a token for the new business", async () => {
  localStorage.setItem("abastalia.refreshToken", "saved");
  const { fetch } = renderInvite((path, method) =>
    path === "/api/invites/preview" ? json(200, preview)
    : path === "/api/invites/accept" && method === "POST" ? json(204)
    : path === "/api/auth/refresh" ? json(200, tokens)
    : path === "/api/me" ? json(200, me)
    : undefined,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Unirme al equipo" }));

  expect(await screen.findByText("Panel")).toBeTruthy();
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/auth/refresh"))).toBe(true);
});

it("TK-217: a used, expired or revoked link says so", async () => {
  renderInvite((path) =>
    path === "/api/invites/preview" ? json(404, { code: "Invites.Invalid", detail: "La invitación no es válida, ya se usó o venció. Pida una nueva al dueño." }) : undefined,
  );

  expect(await screen.findByText("Invitación no válida")).toBeTruthy();
  expect(screen.getByText("La invitación no es válida, ya se usó o venció. Pida una nueva al dueño.")).toBeTruthy();
  expect(screen.queryByLabelText(/Contraseña/)).toBeNull();
});

it("TK-217: a wrong account gets the server's message", async () => {
  localStorage.setItem("abastalia.refreshToken", "saved");
  renderInvite((path) =>
    path === "/api/invites/preview" ? json(200, preview)
    : path === "/api/invites/accept" ? json(403, { code: "Invites.WrongEmail", detail: "Esta invitación es para contable@example.com. Entre con esa cuenta." })
    : undefined,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Unirme al equipo" }));

  expect(await screen.findByText("Esta invitación es para contable@example.com. Entre con esa cuenta.")).toBeTruthy();
  expect(screen.getByRole("button", { name: "Usar otra cuenta" })).toBeTruthy();
});

it("TK-230: someone with an account taps Entre y únase, signs in with the invited e-mail and joins", async () => {
  const { fetch } = renderInvite((path, method) =>
    path === "/api/invites/preview" ? json(200, preview)
    : path === "/api/auth/login" ? json(200, tokens)
    : path === "/api/invites/accept" && method === "POST" ? json(204)
    : path === "/api/auth/refresh" ? json(200, tokens)
    : path === "/api/me" ? json(200, me)
    : undefined,
  );

  fireEvent.click(await screen.findByRole("button", { name: "Entre y únase" }));
  const password = screen.getByLabelText("Contraseña") as HTMLInputElement;
  fireEvent.change(password, { target: { value: "Carniceria2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Mostrar contraseña" }));
  expect(password.type).toBe("text");
  fireEvent.click(screen.getByRole("button", { name: "Entrar y unirme" }));

  expect(await screen.findByText("Panel")).toBeTruthy();
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/invites/register"))).toBe(false);
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/invites/accept"))).toBe(true);
});
