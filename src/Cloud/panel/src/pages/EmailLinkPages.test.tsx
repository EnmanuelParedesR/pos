import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { StrictMode } from "react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router";
import { json } from "../testing";
import { ConfirmEmailPage, ResetPasswordPage } from "./EmailLinkPages";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderAt(entry: string, routes: (path: string) => Response | undefined) {
  const fetch = vi.fn(async (url: string) => routes(url.replace(/^https?:\/\/[^/]+/, "")) ?? json(404, { detail: "NotMocked" }));
  vi.stubGlobal("fetch", fetch);
  render(
    <StrictMode>
      <MemoryRouter initialEntries={[entry]}>
        <Routes>
          <Route path="/app/restablecer" element={<ResetPasswordPage />} />
          <Route path="/app/confirmar" element={<ConfirmEmailPage />} />
          <Route path="/app" element={<SignInStub />} />
        </Routes>
      </MemoryRouter>
    </StrictMode>,
  );
  return fetch;
}

/** What /app receives from the confirm page (router state). */
function SignInStub() {
  const state = useLocation().state as { email?: string } | null;
  return <p>Entrar: {state?.email}</p>;
}

it("TK-230: the reset link (code after #) sets a new password with Identity's resetPassword", async () => {
  const fetch = renderAt("/app/restablecer#email=due%C3%B1a%2Bpos%40example.com&code=Q0RF", (path) => (path === "/api/auth/resetPassword" ? json(200) : undefined));
  expect(screen.getByText("Para dueña+pos@example.com")).toBeTruthy();
  fireEvent.change(screen.getByLabelText("Contraseña nueva"), { target: { value: "Nueva2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Guardar contraseña" }));

  expect(await screen.findByText("Listo, su contraseña cambió.")).toBeTruthy();
  const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toMatch(/\/api\/auth\/resetPassword$/);
  expect(init.body).toBe(JSON.stringify({ email: "dueña+pos@example.com", resetCode: "Q0RF", newPassword: "Nueva2026" }));
});

it("TK-230: a used or expired reset code asks for a new link", async () => {
  renderAt("/app/restablecer#email=a%40example.com&code=old", (path) =>
    path === "/api/auth/resetPassword" ? json(400, { errors: { InvalidToken: ["Invalid token."] } }) : undefined,
  );
  fireEvent.change(screen.getByLabelText("Contraseña nueva"), { target: { value: "Nueva2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Guardar contraseña" }));
  expect(await screen.findByText("El enlace no es válido o ya venció. Pida uno nuevo.")).toBeTruthy();
});

it("TK-230: a link without its code says it is incomplete", () => {
  renderAt("/app/restablecer", () => undefined);
  expect(screen.getByText("El enlace está incompleto.")).toBeTruthy();
  expect(screen.queryByLabelText("Contraseña nueva")).toBeNull();
});

it("TK-230, R11, TK-297: the link activates the account with the code in the body, warns about the password and hands the e-mail to Entrar", async () => {
  const fetch = renderAt("/app/confirmar#userId=u1&code=c1", (path) =>
    path === "/api/auth/confirm" ? json(200, { firstConfirmation: true, email: "duena@minimercado.com" }) : undefined,
  );
  expect(await screen.findByRole("heading", { name: "Su cuenta quedó activada" })).toBeTruthy();
  expect(screen.getByText("duena@minimercado.com")).toBeTruthy();
  expect(screen.getByText("¿No creó usted esta cuenta?")).toBeTruthy();
  const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toMatch(/\/api\/auth\/confirm$/);
  expect(JSON.parse(String(init.body))).toEqual({ userId: "u1", code: "c1" });

  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));
  expect(await screen.findByText("Entrar: duena@minimercado.com")).toBeTruthy();
  expect(fetch.mock.calls.filter(([u]) => String(u).endsWith("/api/auth/confirm"))).toHaveLength(1); // one request, StrictMode or not
});

it("TK-297: a link used before says the account was already active, not that it failed", async () => {
  renderAt("/app/confirmar#userId=u2&code=c2", (path) => (path === "/api/auth/confirm" ? json(200, { firstConfirmation: false, alreadyConfirmed: true }) : undefined));
  expect(await screen.findByRole("heading", { name: "Su cuenta ya estaba activada" })).toBeTruthy();
  expect(screen.queryByText("¿No creó usted esta cuenta?")).toBeNull();
});

it("TK-297: a bad link explains what to do next", async () => {
  renderAt("/app/confirmar#userId=u3&code=c3", (path) => (path === "/api/auth/confirm" ? json(401) : undefined));
  expect(await screen.findByRole("heading", { name: "No pudimos activar la cuenta" })).toBeTruthy();
  expect(screen.getByText("El enlace no es válido o venció.")).toBeTruthy();
});

it("TK-297: a link without its code calls nothing", () => {
  const fetch = renderAt("/app/confirmar", () => undefined);
  expect(screen.getByRole("heading", { name: "El enlace está incompleto" })).toBeTruthy();
  expect(fetch).not.toHaveBeenCalled();
});
