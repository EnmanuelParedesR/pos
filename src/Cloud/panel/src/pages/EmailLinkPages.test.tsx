import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router";
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
    <MemoryRouter initialEntries={[entry]}>
      <Routes>
        <Route path="/app/restablecer" element={<ResetPasswordPage />} />
        <Route path="/app/confirmar" element={<ConfirmEmailPage />} />
      </Routes>
    </MemoryRouter>,
  );
  return fetch;
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

it("TK-230: the sign-up e-mail's link confirms the address", async () => {
  const fetch = renderAt("/app/confirmar#userId=u1&code=c1", (path) => (path.startsWith("/api/auth/confirmEmail?") ? json(200) : undefined));
  expect(await screen.findByText("Listo, su correo quedó confirmado.")).toBeTruthy();
  expect(String(fetch.mock.calls[0][0])).toMatch(/\/api\/auth\/confirmEmail\?userId=u1&code=c1$/);
});
