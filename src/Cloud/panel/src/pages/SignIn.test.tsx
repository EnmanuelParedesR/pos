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

it("TK-230: an e-mail that already has an account says so, and its link switches to Entrar keeping the e-mail", async () => {
  const { onSignedIn } = renderSignIn((path) =>
    path === "/api/auth/register"
      ? json(400, { title: "One or more validation errors occurred.", errors: { DuplicateUserName: ["x"], DuplicateEmail: ["x"] } })
      : undefined,
  );
  fireEvent.click(tab("Crear cuenta"));
  fireEvent.change(screen.getByLabelText("Correo"), { target: { value: "dueno@carniceria.com" } });
  fireEvent.change(screen.getByLabelText("Contraseña"), { target: { value: "Carne2026" } });
  fireEvent.click(screen.getByRole("button", { name: "Crear cuenta" }));

  const alert = await screen.findByRole("alert");
  expect(alert.textContent).toContain("Ese correo ya tiene una cuenta.");
  expect(screen.getByLabelText("Correo").getAttribute("aria-invalid")).toBe("true");
  fireEvent.click(within(alert).getByRole("button", { name: "Entre con su contraseña" }));

  expect(tab("Entrar").getAttribute("aria-selected")).toBe("true");
  expect(screen.queryByRole("alert")).toBeNull();
  expect((screen.getByLabelText("Correo") as HTMLInputElement).value).toBe("dueno@carniceria.com");
  expect(document.activeElement).toBe(screen.getByLabelText("Contraseña"));
  expect(onSignedIn).not.toHaveBeenCalled();
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
