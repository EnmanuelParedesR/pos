import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import type { PublicCatalog } from "../api";
import { App } from "../App";
import { json } from "../testing";
import { orderMessage, whatsAppLink } from "./CatalogPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const catalog: PublicCatalog = {
  storeName: "Carnicería Don Pepe",
  whatsApp: "18095550101",
  items: [
    { productId: "p1", name: "Bistec de res", category: "Res", unit: "Pound", price: 275 },
    { productId: "p2", name: "Salami", category: "Embutidos", unit: "Unit", price: 150 },
  ],
};

it("TK-195: the catalog opens without a session and the WhatsApp button carries what was picked", async () => {
  localStorage.setItem("abastalia.refreshToken", "old"); // even with an owner's session saved, the public page never asks for it
  const fetch = vi.fn(async () => json(200, catalog));
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={["/c/7f1c0d2e-0000-7000-8000-000000000001"]}>
      <App />
    </MemoryRouter>,
  );

  expect(await screen.findByRole("heading", { name: "Carnicería Don Pepe" })).toBeTruthy();
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(String((fetch.mock.calls[0] as unknown[])[0])).toMatch(/\/api\/public\/catalog\/7f1c0d2e-0000-7000-8000-000000000001$/);
  expect(screen.getByText("Toque + en lo que quiere pedir.")).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "Más Bistec de res" }));
  fireEvent.click(screen.getByRole("button", { name: "Más Bistec de res" }));
  fireEvent.click(screen.getByRole("button", { name: "Más Bistec de res" }));
  fireEvent.click(screen.getByRole("button", { name: "Más Salami" }));
  expect(screen.getByText("1.5 lb")).toBeTruthy();

  const link = screen.getByRole("link", { name: /Pedir por WhatsApp \(2\)/ }) as HTMLAnchorElement;
  expect(link.href.startsWith("https://wa.me/18095550101?text=")).toBe(true);
  const text = decodeURIComponent(link.href.split("text=")[1]);
  expect(text).toContain("• 1.5 lb Bistec de res (RD$ 275.00/lb)");
  expect(text).toContain("• 1 und. Salami (RD$ 150.00/und.)");
  expect(text).toContain("Total aproximado: RD$ 562.50");
});

it("TK-195: without a WhatsApp number the page says so instead of a broken button", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(200, { ...catalog, whatsApp: null })));
  render(
    <MemoryRouter initialEntries={["/c/s1"]}>
      <App />
    </MemoryRouter>,
  );
  expect(await screen.findByText("Esta tienda todavía no recibe pedidos por WhatsApp.")).toBeTruthy();
  expect(screen.queryByRole("link", { name: /WhatsApp/ })).toBeNull();
});

it("TK-195: the message and the link only keep digits of the phone", () => {
  expect(orderMessage("Colmado", catalog.items, { p2: 2 })).toBe("Hola, Colmado. Quiero pedir:\n• 2 und. Salami (RD$ 150.00/und.)\nTotal aproximado: RD$ 300.00");
  expect(whatsAppLink("+1 (809) 555-0101", "hola")).toBe("https://wa.me/18095550101?text=hola");
});
