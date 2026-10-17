import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { json } from "../testing";
import { otpauthUri, SecurityPage } from "./SecurityPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const off = { sharedKey: "JBSWY3DPEHPK3PXP", recoveryCodesLeft: 0, isTwoFactorEnabled: false, isMachineRemembered: false };
const on = { ...off, recoveryCodesLeft: 10, isTwoFactorEnabled: true };
const codes = ["AAAAA-11111", "BBBBB-22222"];

/** `answer` gets each /manage/2fa body in turn. */
function renderPage(answer: (body: Record<string, unknown>) => Response) {
  const bodies: Record<string, unknown>[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith("/api/me")) return json(200, { email: "duena@minimercado.com" });
      const body = JSON.parse(String(init?.body ?? "{}"));
      bodies.push(body);
      return answer(body);
    }),
  );
  render(
    <MemoryRouter>
      <SecurityPage />
    </MemoryRouter>,
  );
  return bodies;
}

it("TK-298: the QR carries the key URI apps read", () => {
  expect(otpauthUri("JBSWY3DPEHPK3PXP", "duena@minimercado.com")).toBe(
    "otpauth://totp/Abastalia%3Aduena%40minimercado.com?secret=JBSWY3DPEHPK3PXP&issuer=Abastalia&digits=6",
  );
});

it("TK-298: setup shows the QR and the key, and the backup codes once until the owner says they are saved", async () => {
  const bodies = renderPage((b) => (b.enable === true ? json(200, { ...on, recoveryCodes: codes }) : json(200, off)));
  expect(await screen.findByRole("img", { name: /Código QR/ })).toBeTruthy();
  expect(screen.getByText("JBSW Y3DP EHPK 3PXP")).toBeTruthy();

  fireEvent.change(screen.getByLabelText(/Escriba el código de 6 dígitos/), { target: { value: "123 456" } });
  fireEvent.click(screen.getByRole("button", { name: "Activar" }));
  expect(await screen.findByText("AAAAA-11111")).toBeTruthy();
  expect(screen.getByText("Solo se muestran esta vez.")).toBeTruthy();
  expect(bodies[1]).toEqual({ enable: true, twoFactorCode: "123456" });

  const done = screen.getByRole("button", { name: "Listo" }) as HTMLButtonElement;
  expect(done.disabled).toBe(true);
  fireEvent.click(screen.getByLabelText("Ya guardé los códigos"));
  fireEvent.click(done);
  expect(screen.queryByText("AAAAA-11111")).toBeNull();
  expect(screen.getByText(/Le quedan/)).toBeTruthy();
});

it("TK-298: turning it off asks for the current code and says when it is wrong", async () => {
  const bodies = renderPage((b) => (b.enable === false ? (b.twoFactorCode === "654321" ? json(200, off) : json(400, { errors: { InvalidTwoFactorCode: ["x"] } })) : json(200, on)));
  fireEvent.click(await screen.findByRole("button", { name: "Desactivar" }));
  const input = screen.getByLabelText(/escriba el código de la aplicación o un código de respaldo/);
  fireEvent.change(input, { target: { value: "111111" } });
  fireEvent.click(screen.getByRole("button", { name: "Desactivar" }));
  expect(await screen.findByText(/El código no es válido. Use el de 6 dígitos/)).toBeTruthy();

  fireEvent.change(input, { target: { value: "654321" } });
  fireEvent.click(screen.getByRole("button", { name: "Desactivar" }));
  expect(await screen.findByRole("button", { name: "Activar" })).toBeTruthy();
  expect(bodies.at(-1)).toEqual({ enable: false, twoFactorCode: "654321" });
});

it("TK-298: few backup codes left and the lost-phone path are on the page", async () => {
  renderPage(() => json(200, { ...on, recoveryCodesLeft: 2 }));
  expect(await screen.findByText(/Pida nuevos antes de quedarse sin ninguno/)).toBeTruthy();
  expect(screen.getByRole("heading", { name: "¿Perdió el teléfono?" })).toBeTruthy();
});
