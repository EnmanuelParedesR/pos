import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router";
import { App } from "../../App";
import "../../Panel"; // TK-278: the panel is lazy in App; loading it here keeps the first findBy from waiting on the import.
import type { Me } from "../../api";
import { json } from "../../testing";
import { preparation, sitePages } from "./SitePages";
import { invoiceCost } from "./livePrices";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const owner: Me = { email: "dueno@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "Owner", tenants: [] };

function Where() {
  const { pathname } = useLocation();
  return <output data-testid="where">{pathname}</output>;
}

function renderAt(path: string, me?: Me) {
  if (me) localStorage.setItem("abastalia.refreshToken", "saved");
  const fetch = vi.fn(async (url: string) => (url.endsWith("/api/me") ? json(200, me) : url.endsWith("/api/auth/refresh") ? json(200, { accessToken: "a", refreshToken: "r" }) : url.endsWith("/api/stores") ? json(200, []) : json(404)));
  vi.stubGlobal("fetch", fetch);
  render(
    <MemoryRouter initialEntries={[path]}>
      <App />
      <Where />
    </MemoryRouter>,
  );
  return fetch;
}

it.each(sitePages.map((p) => [p.path, p.title]))("TK-228: %s is public: it renders without a session, never asks for the account and sets its title", (path, title) => {
  localStorage.setItem("abastalia.refreshToken", "saved"); // even signed in, the site never asks for the account
  const fetch = renderAt(path);

  expect(screen.getByRole("heading", { level: 1 })).toBeTruthy();
  expect(screen.getByRole("navigation", { name: "Principal" })).toBeTruthy();
  expect(document.title).toBe(title);
  // TK-249: at most the public prices (no account); never /api/me or anything signed in.
  expect(fetch.mock.calls.every(([url]) => String(url).endsWith("/api/public/prices"))).toBe(true);
});

it("TK-249: /precios shows the prices Paddle has, and keeps the placeholder for a plan without one", async () => {
  const prices = {
    enabled: true,
    plans: [
      { code: "Default", name: "Estándar", monthly: { priceId: "pri_1", interval: "Monthly", amount: "1500", currency: "USD" }, annual: null },
      { code: "MultiRegister", name: "Pro", monthly: { priceId: "pri_2", interval: "Monthly", amount: "2900", currency: "USD" }, annual: null },
    ],
    addOns: [{ code: "ExtraCashier", name: "Cajero adicional", monthly: { priceId: "pri_3", interval: "Monthly", amount: "200", currency: "USD" }, annual: null }],
  };
  vi.stubGlobal("fetch", vi.fn(async (url: string) => (url.endsWith("/api/public/prices") ? json(200, prices) : json(404))));
  render(
    <MemoryRouter initialEntries={["/precios"]}>
      <App />
    </MemoryRouter>,
  );

  // TK-313: RD$ first (RD$61.5 per US$1, rounded), and the US$ Paddle charges under it.
  const standard = screen.getByRole("heading", { name: "Estándar" }).closest(".card") as HTMLElement;
  expect(await within(standard).findByText("RD$925 al mes")).toBeTruthy();
  expect(within(standard).getByText("Se cobra US$15 con tarjeta. Precio final.")).toBeTruthy();
  expect(screen.getByText("RD$1,780 al mes")).toBeTruthy();
  // TK-277: cashiers included per plan, and the extra cashier's price in "Adapte su plan".
  expect(["2 cajeros incluidos", "4 cajeros incluidos", "8 cajeros incluidos"].every((t) => screen.getByText(t))).toBe(true);
  expect(within(screen.getByText("Cajero adicional").closest("tr")!).getByText("RD$125 al mes")).toBeTruthy();
  expect(within(screen.getByText("Cajero adicional").closest("tr")!).getByText("Se cobra US$2 con tarjeta. Precio final.")).toBeTruthy();
  expect(screen.getAllByText("Precio por confirmar").length).toBe(1); // Esencial has no price in Paddle yet
});

it("TK-293: with billing off, /precios shows the provisional prices, marked as such; only what has no price keeps the placeholder", () => {
  renderAt("/precios");
  expect(screen.queryByText("Precio por confirmar")).toBeNull();
  // TK-313: the RD$ of the price list (RD$61.5 per US$1) and the US$ charged.
  for (const [plan, label, usd] of [["Esencial", "RD$925 al mes", "15"], ["Estándar", "RD$1,170 al mes", "19"], ["Pro", "RD$2,150 al mes", "35"]]) {
    const card = within(screen.getByRole("heading", { name: plan }).closest(".card") as HTMLElement);
    expect(card.getByText(label)).toBeTruthy();
    expect(card.getByText(`Se cobra US$${usd} con tarjeta. Precio final.`)).toBeTruthy();
  }
  for (const [addOn, label] of [["Caja adicional", "RD$370 al mes"], ["Cajero adicional", "RD$125 al mes"], ["Sucursal adicional", "RD$1,540 al mes"], ["Lectura de facturas", "RD$310 al mes"], ["Asistente +1,000 preguntas", "RD$310 al mes"], ["Usuarios del panel +5", "RD$185 al mes"]])
    expect(within(screen.getByText(addOn).closest("tr")!).getByText(label)).toBeTruthy();
  expect(within(screen.getByText("Sucursal adicional").closest("tr")!).getByText(/Solo con el plan Pro/)).toBeTruthy();
  expect(screen.getByText(/Precios provisionales/)).toBeTruthy();
  const accompaniment = screen.getByText("Acompañamiento en la certificación e-CF").closest("tr")!;
  expect(within(accompaniment).getByText(/US\$49/)).toBeTruthy();
  expect(screen.getByText("Acompañamiento gratis con cualquier plan pagado antes del 31/12/2026.")).toBeTruthy(); // the launch offer  expect(screen.getByRole("link", { name: "Crear su cuenta y elegir plan" }).getAttribute("href")).toBe("/app/billing"); // TK-249
});

it("TK-242: /descargar shows both roles and the installer placeholder until it is hosted; the menu and /certificacion link to it", () => {
  renderAt("/descargar");
  expect(screen.getByRole("heading", { name: "PC principal" })).toBeTruthy();
  expect(screen.getByRole("heading", { name: "Caja" })).toBeTruthy();
  expect(screen.getByText("[URL DEL INSTALADOR]")).toBeTruthy();
  expect(screen.getAllByRole("link", { name: "Descargar" })[0].getAttribute("href")).toBe("/descargar");
  cleanup();
  renderAt("/certificacion");
  expect(screen.getByRole("link", { name: "Descargar Abastalia" }).getAttribute("href")).toBe("/descargar");
});

it("TK-314: /descargar lists minimum and recommended requirements per PC, the network, the devices and the certificate", () => {
  renderAt("/descargar");
  const main = screen.getByRole("table", { name: "PC principal" });
  expect(within(main).getAllByRole("columnheader").map((h) => h.textContent)).toEqual(["Componente", "Mínimo", "Recomendado"]);
  expect(within(main).getByRole("rowheader", { name: "Memoria" }).parentElement?.textContent).toContain("8 GB");
  expect(within(main).getByText(/versión 1809/)).toBeTruthy(); // the installer's MinVersion 10.0.17763
  expect(screen.getByRole("table", { name: "Cada caja" })).toBeTruthy();
  expect(screen.getByText("Privada")).toBeTruthy(); // TK-292: registers do not reach a main PC on a Public network
  expect(screen.getByRole("rowheader", { name: "Balanza" }).parentElement?.textContent).toContain("Mettler Toledo 8217");
  expect(screen.getByRole("link", { name: "Cómo certificarse con Abastalia" }).getAttribute("href")).toBe("/certificacion");
});

it("TK-228: an unknown address shows the site's 404", () => {
  renderAt("/no-existe");
  expect(screen.getByRole("heading", { name: "No encontramos esa página." })).toBeTruthy();
});

it("TK-228: /app asks for the account when there is no session", async () => {
  renderAt("/app");
  expect(await screen.findByRole("heading", { name: "Bienvenido de nuevo" })).toBeTruthy();
});

it("TK-228: /app with a session opens the panel", async () => {
  renderAt("/app", owner);
  expect(await screen.findAllByText("Carnicería La Esquina")).toBeTruthy();
  expect(screen.getAllByRole("link", { name: "Resumen" })[0].getAttribute("href")).toBe("/app");
});

it("TK-228: old panel links forward to /app, keeping the rest of the address", () => {
  renderAt("/shifts/42?x=1");
  expect(screen.getByTestId("where").textContent).toBe("/app/shifts/42");
  expect(screen.getByRole("heading", { name: "Bienvenido de nuevo" })).toBeTruthy();
});

it("TK-235: /certificacion lists everything the owner prepares before the first session", () => {
  renderAt("/certificacion");
  const list = document.getElementById("preparacion")!;
  for (const item of preparation) expect(list.textContent).toContain(item);
  expect(list.textContent).toContain(".p12 o .pfx");
  expect(list.textContent).toContain("Abastalia nunca le pide esa clave");
  expect(screen.getByText("Acompañamiento gratis con cualquier plan pagado antes del 31/12/2026.")).toBeTruthy(); // TK-313
});

it("TK-245: the home page shows the product: deadline banner, trial and contact calls to action, Talia's demo and no price placeholder", () => {
  renderAt("/");
  expect(screen.getByRole("heading", { level: 1, name: "Pregúntele a su negocio." })).toBeTruthy();
  expect(screen.getByRole("link", { name: /15 de noviembre de 2026/ }).getAttribute("href")).toBe("/e-cf");
  const trials = screen.getAllByRole("link", { name: "Probar 14 días gratis" });
  expect(trials.length).toBe(3); // header, hero, price band
  for (const trial of trials) expect(trial.getAttribute("href")).toBe("/descargar");
  // The owner gave the WhatsApp number (17/10/2026): "Hablemos" became "Hablar por WhatsApp", straight to the chat.
  expect(screen.getByRole("link", { name: /Hablar por WhatsApp/ }).getAttribute("href")).toMatch(/^https:\/\/wa\.me\/18496313513\?text=/);
  expect(screen.getByText(/Datos de ejemplo/)).toBeTruthy(); // TK-312: Talia's answers are examples
  expect(screen.queryByText("Precio por confirmar")).toBeNull(); // TK-274: no placeholder on the home
  expect(screen.getByRole("heading", { level: 2, name: "Desde RD$925 al mes" })).toBeTruthy(); // TK-293, TK-313: the provisional entry price
  expect(screen.getByText("Se cobra US$15 con tarjeta. Precio final. Precios provisionales.")).toBeTruthy();
  expect(screen.getByRole("link", { name: "¿Necesita certificarse? Le acompañamos →" }).getAttribute("href")).toBe("/certificacion");
  for (const [name, href] of [["Carnicería", "/carniceria"], ["Colmado", "/colmado"], ["Deli", "/deli"]])
    expect(screen.getAllByRole("link", { name: new RegExp(`^${name}`) }).some((a) => a.getAttribute("href") === href)).toBe(true);
});

it.each(["/carniceria", "/colmado", "/deli"])("TK-245: %s keeps both calls to action", (path) => {
  renderAt(path);
  expect(screen.getAllByRole("link", { name: "Probar 14 días gratis" }).length).toBeGreaterThan(1);
  expect(screen.getByRole("link", { name: /Hablar por WhatsApp/ })).toBeTruthy();
});

it("TK-263: the e-CF guide, the comparison and the glossary link to each other and from /e-cf, /precios, /certificacion, /novedades and the footer", () => {
  const expectLinks = (path: string, hrefs: string[]) => {
    renderAt(path);
    const all = [...document.querySelectorAll("a")].map((a) => a.getAttribute("href"));
    for (const href of hrefs) expect(all, `${path} → ${href}`).toContain(href);
    cleanup();
  };
  expectLinks("/e-cf", ["/guia-e-cf", "/glosario", "/comparar"]);
  expectLinks("/precios", ["/comparar"]);
  expectLinks("/certificacion", ["/glosario"]);
  expectLinks("/guia-e-cf", ["/e-cf", "/comparar", "/certificacion", "/glosario"]);
  expectLinks("/glosario", ["/guia-e-cf", "/comparar", "/e-cf"]);
  expectLinks("/comparar", ["/precios"]);
  expectLinks("/novedades", ["/guia-e-cf", "/glosario", "/comparar"]);
  renderAt("/");
  const footer = screen.getByRole("navigation", { name: "Pie de página" });
  for (const name of ["Guía e-CF", "Comparar", "Glosario"]) expect(footer.textContent).toContain(name);
});

it("TK-294: /comparar is a labelled carousel: one card per point with Abastalia, the Facturador Gratuito and what to ask another program", () => {
  renderAt("/comparar");
  const carousel = screen.getByRole("region", { name: "Lado a lado" });
  expect(carousel.getAttribute("aria-roledescription")).toBe("carrusel");
  const slides = within(carousel).getAllByRole("group").filter((g) => g.getAttribute("aria-roledescription") === "diapositiva");
  expect(slides.map((s) => s.getAttribute("aria-label"))[0]).toBe("1 de 9: Costo");
  expect(slides.length).toBe(9);
  for (const slide of slides) for (const option of ["Abastalia", "Facturador Gratuito (DGII)", "Otro programa: pregunte"]) expect(within(slide).getByText(option)).toBeTruthy();
  const track = document.getElementById(within(carousel).getByRole("button", { name: "Siguiente" }).getAttribute("aria-controls")!)!;
  expect(track.tabIndex).toBe(0); // the arrow keys scroll it
  expect((within(carousel).getByRole("button", { name: "Anterior" }) as HTMLButtonElement).disabled).toBe(true); // at the start
  expect(screen.getByText(/Actualizado el/).textContent).toContain("16/10/2026"); // the dated sources stay
});

it("TK-245: /e-cf keeps the date, the extension note and the legal disclaimer", () => {
  renderAt("/e-cf");
  expect(screen.getByText("15/11/2026")).toBeTruthy();
  expect(screen.getByText(/prórroga de seis meses/)).toBeTruthy();
  expect(screen.getByText(/no es asesoría legal/)).toBeTruthy();
});

it("TK-296: the home's video never loads or plays by itself, has a descriptions track and a transcript one tap away from the same script (TK-312)", () => {
  renderAt("/");
  const video = screen.getByLabelText(/Video de 42 segundos/) as HTMLVideoElement;
  expect(video.getAttribute("preload")).toBe("none");
  expect(video.hasAttribute("autoplay")).toBe(false);
  expect(video.querySelector("track")?.getAttribute("kind")).toBe("descriptions");
  expect(video.querySelector("track")?.getAttribute("srclang")).toBe("es");
  const transcript = document.getElementById(video.getAttribute("aria-describedby")!)!;
  expect(within(transcript).getByText("Ver lo que muestra el video")).toBeTruthy();
  expect(within(transcript).getByText("Pregúntele a Talia.")).toBeTruthy();
  expect(screen.getByRole("button", { name: "Ver el video (0:42)" })).toBeTruthy();
});

it.each(sitePages.map((p) => [p.path]))("TK-330: %s promises nothing the TK-279 block contradicts", (path) => {
  renderAt(path);
  const text = document.body.textContent ?? "";
  for (const promise of [/solo lectura/i, /pago atrasado/i, /nunca se detiene/i]) expect(text).not.toMatch(promise);
});

it.each(sitePages.map((p) => [p.path]))("TK-312: %s never says \"fiado\" (ventas a crédito, cuentas por cobrar)", (path) => {
  renderAt(path);
  const page = sitePages.find((p) => p.path === path)!;
  expect(`${document.body.textContent} ${page.title} ${page.description}`).not.toMatch(/fiad/i);
});

it("TK-312: the site is light unless dark was picked, and its switch has only claro and oscuro", async () => {
  renderAt("/");
  const toggle = await screen.findByRole("button", { name: "Tema: claro. Cambiar a oscuro" });
  toggle.click();
  expect(await screen.findByRole("button", { name: "Tema: oscuro. Cambiar a claro" })).toBeTruthy();
  expect(localStorage.getItem("abastalia.theme")).toBe("dark");
  expect(document.documentElement.dataset.choice).toBe("dark");
});

it("TK-312: the home opens with Talia answering what her tools answer today; a question button shows its answer and sends nothing", () => {
  const fetch = renderAt("/");
  expect(screen.getByRole("heading", { level: 1 }).textContent).toBe("Pregúntele a su negocio.");
  expect(screen.getByText(/lo más vendido fue Huevos/)).toBeTruthy();
  expect(screen.getByText("Ir a Ventas →")).toBeTruthy(); // TK-339: her button to the screen with the detail
  const calls = fetch.mock.calls.length;
  fireEvent.click(screen.getByRole("button", { name: "¿Cuánto me deben y quién debe más?" }));
  expect(screen.getByText(/Hoy le deben RD\$ 12,840\.00/)).toBeTruthy();
  expect(screen.getByRole("button", { name: "¿Cuánto me deben y quién debe más?" }).getAttribute("aria-pressed")).toBe("true");
  expect(fetch.mock.calls.length).toBe(calls);
  expect(screen.getByText("Datos de ejemplo.", { exact: false })).toBeTruthy();
});

it("TK-312: the six main benefits are on the home, and weighing is not one of them", () => {
  renderAt("/");
  const tiles = [...document.querySelectorAll(".counter .tile h3")].map((h) => h.textContent);
  expect(tiles).toEqual(["Sin internet, sigue vendiendo", "e-CF directo con la DGII", "La caja cuadra", "Talia, su asistente", "Su negocio en el celular", "Clientes y cuentas por cobrar"]);
  expect(document.querySelector("video.loop")).toBeNull(); // the analog-scale loop is gone
});

it("TK-312: the e-CF band shows the real path and the register's own queue chip", () => {
  renderAt("/e-cf");
  expect(screen.getByRole("heading", { name: "Envía a la DGII" })).toBeTruthy();
  expect(screen.getByText("e-CF en cola: 3")).toBeTruthy();
  expect(within(screen.getByRole("list", { name: "Lo que no hay en medio" })).getAllByRole("listitem")).toHaveLength(3);
});

it("TK-316: the calculator's month is a whole number from 0 to 1,000,000, at RD$1 to RD$2 per invoice", () => {
  expect(invoiceCost("3000")).toEqual({ invoices: 3000, low: 3000, high: 6000 });
  for (const typed of ["0", "", "-5", "abc"]) expect(invoiceCost(typed)).toEqual({ invoices: 0, low: 0, high: 0 });
  expect(invoiceCost("12.7").invoices).toBe(12);
  expect(invoiceCost("99999999999")).toEqual({ invoices: 1_000_000, low: 1_000_000, high: 2_000_000 });
  expect(invoiceCost("1e400").invoices).toBe(1_000_000); // Infinity
});

it("TK-316: /precios has the savings calculator: typed invoices give the per-invoice range, RD$0 in Abastalia and the fair use above 50,000", () => {
  renderAt("/precios");
  const input = screen.getByLabelText("Facturas que emite al mes") as HTMLInputElement;
  const result = () => document.querySelector(".calc__result[role=status]")!.textContent;
  expect(input.getAttribute("aria-describedby")).toBe("calc-hint");
  expect(result()).toBe("Con 3,000 facturas al mes se ahorra entre RD$3,000 y RD$6,000 al mes.");
  expect(within(screen.getByRole("heading", { name: "Con Abastalia" }).closest(".card") as HTMLElement).getByText(/desde RD\$925 al mes/)).toBeTruthy();
  fireEvent.change(input, { target: { value: "0" } });
  expect(result()).toBe("Escriba cuántas facturas emite al mes.");
  expect(screen.queryByText(/pasa del uso justo/)).toBeNull();
  fireEvent.change(input, { target: { value: "60000" } });
  expect(result()).toContain("entre RD$60,000 y RD$120,000");
  expect(screen.getByText(/pasa del uso justo/).textContent).toContain("No hay cobro automático");
  expect(document.body.textContent).not.toMatch(/poszy|facturapro|alegra|dgmax|glecf/i); // no brand names
});

it("TK-316: every plan card and /comparar say e-CF ilimitados with the fair-use number", () => {
  renderAt("/precios");
  for (const plan of ["Esencial", "Estándar", "Pro"])
    expect(within(screen.getByRole("heading", { name: plan }).closest(".card") as HTMLElement).getByText("e-CF ilimitados (uso justo 50,000 al mes)")).toBeTruthy();
  cleanup();
  renderAt("/comparar");
  const row = screen.getAllByRole("group").find((g) => g.getAttribute("aria-label")?.endsWith("Facturas al mes"))!;
  expect(within(row).getByText("e-CF ilimitados (uso justo 50,000 al mes), sin cobro por factura")).toBeTruthy();
  expect(screen.getByRole("link", { name: "términos" }).getAttribute("href")).toBe("/terminos#uso-justo");
});

it("TK-316: /terminos has the e-CF fair use: 50,000 a month per business, we call, no automatic charge", () => {
  renderAt("/terminos");
  const clause = document.getElementById("uso-justo")!.textContent!;
  for (const part of ["50,000 e-CF al mes por negocio", "le contactamos para revisar su plan", "No hay cobro automático", "sin cobro por documento"]) expect(clause).toContain(part);
});
