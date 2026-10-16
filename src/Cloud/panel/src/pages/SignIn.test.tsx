import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { json } from "../testing";
import { SignIn } from "./SignIn";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

function renderSignIn(routes: (path: string) => Response | undefined = () => undefined) {
  const fetch = vi.fn(async (url: string) => {
    const path = url.replace(/^https?:\/\/[^/]+/, "");
    return routes(path) ?? json(404, { code: "NotMocked", detail: path });
  });
  vi.stubGlobal("fetch", fetch);
  const onSignedIn = vi.fn(async () => {});
  render(
    <MemoryRouter>
      <SignIn onSignedIn={onSignedIn} />
    </MemoryRouter>,
  );
  return { fetch, onSignedIn };
}

const tab = (name: string) => screen.getByRole("tab", { name });

it("TK-230: Entrar and Crear cuenta are tabs; arrows, Home and End move between them", () => {
  renderSignIn();
  expect(tab("Entrar").getAttribute("aria-selected")).toBe("true");
  expect(tab("Crear cuenta").tabIndex).toBe(-1);
  expect(screen.getByRole("tabpanel").getAttribute("aria-labelledby")).toBe(tab("Entrar").id);

  fireEvent.keyDown(tab("Entrar"), { key: "ArrowRight" });
  expect(tab("Crear cuenta").getAttribute("aria-selected")).toBe("true");
  expect(document.activeElement).toBe(tab("Crear cuenta"));
  expect(screen.getByRole("heading", { name: "Cree su cuenta" })).toBeTruthy();

  fireEvent.keyDown(tab("Crear cuenta"), { key: "ArrowRight" }); // wraps around
  expect(tab("Entrar").getAttribute("aria-selected")).toBe("true");
  fireEvent.keyDown(tab("Entrar"), { key: "End" });
  expect(tab("Crear cuenta").getAttribute("aria-selected")).toBe("true");
  fireEvent.keyDown(tab("Crear cuenta"), { key: "Home" });
  expect(tab("Entrar").getAttribute("aria-selected")).toBe("true");
});

it("TK-230: Mostrar shows the password and Ocultar hides it again", () => {
  renderSignIn();
  const input = screen.getByLabelText("Contraseña") as HTMLInputElement;
  expect(input.type).toBe("password");
  fireEvent.click(screen.getByRole("button", { name: "Mostrar contraseña" }));
  expect(input.type).toBe("text");
  fireEvent.click(screen.getByRole("button", { name: "Ocultar contraseña" }));
  expect(input.type).toBe("password");
});

it("TK-230: creating an account checks the server's password rules while typing", () => {
  renderSignIn();
  fireEvent.click(tab("Crear cuenta"));
  const checks = screen.getByRole("list", { name: "La contraseña necesita" });
  const state = () => within(checks).getAllByRole("listitem").map((li) => [li.textContent, li.classList.contains("is-met")]);

  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "carne" } });
  expect(state()).toEqual([
    ["8 caracteres o más: falta", false],
    ["Una mayúscula: falta", false],
    ["Una minúscula: listo", true],
    ["Un número: falta", false],
  ]);
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carne2026" } });
  expect(state().every(([, met]) => met)).toBe(true);
});

it("R11: creating an account asks to confirm the e-mail first and switches to Entrar keeping the e-mail", async () => {
  const { fetch, onSignedIn } = renderSignIn((path) => (path === "/api/auth/register" || path === "/api/auth/resendConfirmationEmail" ? json(200) : undefined));
  fireEvent.click(tab("Crear cuenta"));
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "dueno@carniceria.com" } });
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carne2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Crear cuenta" }));

  expect(await screen.findByText("Confirme su correo para entrar.")).toBeTruthy();
  expect(tab("Entrar").getAttribute("aria-selected")).toBe("true");
  expect((screen.getByLabelText("Correo") as HTMLInputElement).value).toBe("dueno@carniceria.com");
  expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/auth/login"))).toBe(false); // it cannot sign in yet
  fireEvent.click(screen.getByRole("button", { name: "pida otro" }));
  await vi.waitFor(() => expect(fetch.mock.calls.some(([url]) => String(url).endsWith("/api/auth/resendConfirmationEmail"))).toBe(true));
  expect(onSignedIn).not.toHaveBeenCalled();
});

it("R11: an unconfirmed e-mail is told to confirm it", async () => {
  renderSignIn((path) => (path === "/api/auth/login" ? json(401, { detail: "NotAllowed" }) : undefined));
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "dueno@carniceria.com" } });
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carne2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));
  expect(await screen.findByText("Confirme su correo para entrar.")).toBeTruthy();
});

it("R11: an account with two-step verification asks for the app's code and sends it with the password", async () => {
  let calls = 0;
  const { fetch, onSignedIn } = renderSignIn((path) =>
    path === "/api/auth/login" ? (++calls === 1 ? json(401, { detail: "RequiresTwoFactor" }) : json(200, { accessToken: "a", refreshToken: "r" })) : undefined,
  );
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "staff@abastalia.com" } });
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carne2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));

  fireEvent.change(await screen.findByLabelText("Código de verificación"), { target: { value: "123456" } });
  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));
  await vi.waitFor(() => expect(onSignedIn).toHaveBeenCalled());
  const [, init] = fetch.mock.calls.filter(([url]) => String(url).endsWith("/api/auth/login"))[1] as unknown as [string, RequestInit];
  expect(JSON.parse(String(init.body))).toEqual({ email: "staff@abastalia.com", password: "Carne2026", twoFactorCode: "123456" });
});

it("TK-230: ¿La olvidó? asks for the reset e-mail and answers the same whether the account exists or not", async () => {
  const { fetch } = renderSignIn((path) => (path === "/api/auth/forgotPassword" ? json(200) : undefined));
  fireEvent.click(screen.getByRole("button", { name: "¿La olvidó?" }));
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: " nadie@example.com " } });
  fireEvent.click(screen.getByRole("button", { name: "Mandar enlace" }));

  expect(await screen.findByText("Revise su correo.")).toBeTruthy();
  expect(screen.getByText(/Si hay una cuenta con nadie@example.com/)).toBeTruthy();
  const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toMatch(/\/api\/auth\/forgotPassword$/);
  expect(init.body).toBe(JSON.stringify({ email: "nadie@example.com" }));
  // TK-231: an unconfirmed address gets nothing; the page says where to ask.
  const note = screen.getByText(/puede que su cuenta no tenga el correo confirmado/);
  expect(within(note).getByRole("link", { name: /Escríbanos/ })).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "← Volver a Entrar" }));
  expect(tab("Entrar").getAttribute("aria-selected")).toBe("true");
});

it("signs in and hands over to the panel", async () => {
  const { onSignedIn } = renderSignIn((path) => (path === "/api/auth/login" ? json(200, { accessToken: "a", refreshToken: "r" }) : undefined));
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "duena@esquina.do" } });
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carniceria2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));
  await vi.waitFor(() => expect(onSignedIn).toHaveBeenCalled());
});

it("TK-276: signing in sends the code kept from /precios?ref= so the store's trial is 30 days, and a failure does not stop it", async () => {
  localStorage.setItem("abastalia.ref", "ABCD2345");
  const { fetch, onSignedIn } = renderSignIn((path) => (path === "/api/auth/login" ? json(200, { accessToken: "a", refreshToken: "r" }) : json(403, { code: "Forbidden" })));
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "duena@esquina.do" } });
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carniceria2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));
  await vi.waitFor(() => expect(onSignedIn).toHaveBeenCalled());
  const claim = fetch.mock.calls.find(([url]) => String(url).endsWith("/api/billing/referrals/claim")) as unknown as [string, RequestInit];
  expect(JSON.parse(String(claim[1].body))).toEqual({ code: "ABCD2345" });
});
