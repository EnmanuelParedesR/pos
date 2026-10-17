import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { SiteGate } from "./SiteGate";

// SHA-256 of "abc" (a test code; the real one never goes in the repo).
const abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

beforeEach(() => vi.stubEnv("VITE_SITE_GATE", abc));
afterEach(() => {
  cleanup();
  vi.unstubAllEnvs();
  localStorage.clear();
});

const gate = () =>
  render(
    <SiteGate>
      <p>Contenido del sitio</p>
    </SiteGate>,
  );

function enter(code: string) {
  fireEvent.change(screen.getByLabelText("Código de acceso"), { target: { value: code } });
  fireEvent.click(screen.getByRole("button", { name: "Entrar" }));
}

it("TK-285: a wrong code shows an inline error and keeps the site closed", async () => {
  gate();
  expect(screen.getByRole("heading", { name: "Sitio en preparación" })).toBeTruthy();
  enter("xyz");
  expect((await screen.findByRole("alert")).textContent).toContain("no es correcto");
  expect(screen.getByLabelText("Código de acceso").getAttribute("aria-invalid")).toBe("true");
  expect(screen.queryByText("Contenido del sitio")).toBeNull();
});

it("TK-285: the right code opens the site and is remembered on the next visit", async () => {
  gate();
  enter(" abc ");
  expect(await screen.findByText("Contenido del sitio")).toBeTruthy();
  expect(localStorage.getItem("abastalia.siteGate")).toBe(abc);
  cleanup();
  gate();
  expect(await screen.findByText("Contenido del sitio")).toBeTruthy();
});

it("TK-285: a remembered code from another gate does not open it", () => {
  localStorage.setItem("abastalia.siteGate", "0".repeat(64));
  gate();
  expect(screen.queryByText("Contenido del sitio")).toBeNull();
});

it("TK-285: no gate configured, the site shows as today", () => {
  vi.stubEnv("VITE_SITE_GATE", "");
  gate();
  expect(screen.getByText("Contenido del sitio")).toBeTruthy();
});
