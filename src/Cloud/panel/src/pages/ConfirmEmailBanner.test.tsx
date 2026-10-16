import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { Me } from "../api";
import { json } from "../testing";
import { ConfirmEmailBanner } from "./ConfirmEmailBanner";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const me = (emailConfirmed: boolean): Me => ({
  email: "dueno@example.com",
  tenantId: "t1",
  tenantName: "Carnicería",
  role: "Owner",
  tenants: [],
  emailConfirmed,
});

const title = "Confirme su correo para poder recuperar la contraseña";

it("TK-231: the banner shows only while the e-mail is unconfirmed", () => {
  const { rerender } = render(<ConfirmEmailBanner me={me(true)} />);
  expect(screen.queryByText(title)).toBeNull();
  rerender(<ConfirmEmailBanner me={me(false)} />);
  expect(screen.getByText(title)).toBeTruthy();
});

it("TK-231: Reenviar correo asks the API once and answers without saying whether the account exists", async () => {
  const fetch = vi.fn(async () => json(200));
  vi.stubGlobal("fetch", fetch);
  render(<ConfirmEmailBanner me={me(false)} />);

  fireEvent.click(screen.getByRole("button", { name: "Reenviar correo" }));

  expect(await screen.findByText("Listo. Revise su correo.")).toBeTruthy();
  expect(screen.getByText(/Si su correo todavía necesita confirmarse/)).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Reenviar correo" })).toBeNull(); // one per page load
  expect(fetch).toHaveBeenCalledTimes(1);
  const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit];
  expect(url).toMatch(/\/api\/auth\/resendConfirmationEmail$/);
  expect(JSON.parse(init.body as string)).toEqual({ email: "dueno@example.com" });
});

it("TK-231: too many requests asks to wait", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(429)));
  render(<ConfirmEmailBanner me={me(false)} />);
  fireEvent.click(screen.getByRole("button", { name: "Reenviar correo" }));
  expect(await screen.findByText(/Espere unos minutos/)).toBeTruthy();
});
