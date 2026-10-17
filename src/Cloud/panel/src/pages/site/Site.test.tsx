import { afterEach, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router";
import { App } from "../../App";
import "../../Panel"; // TK-278: the panel is lazy in App; loading it here keeps the first findBy from waiting on the import.
import type { Me } from "../../api";
import { json } from "../../testing";
import { preparation, sitePages } from "./SitePages";

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

  expect((await screen.findAllByText("US$15.00 al mes")).length).toBeGreaterThan(0);
  expect(screen.getByText("US$29.00 al mes")).toBeTruthy();
  // TK-277: cashiers included per plan, and the extra cashier's price in "Adapte su plan".
  expect(["2 cajeros incluidos", "4 cajeros incluidos", "8 cajeros incluidos"].every((t) => screen.getByText(t))).toBe(true);
  expect(within(screen.getByText("Cajero adicional").closest("tr")!).getByText("US$2.00 al mes")).toBeTruthy();
  expect(screen.getAllByText("Precio por confirmar").length).toBe(1); // Esencial has no price in Paddle yet
});

it("TK-293: with billing off, /precios shows the provisional prices, marked as such; only what has no price keeps the placeholder", () => {
  renderAt("/precios");
  expect(screen.queryByText("Precio por confirmar")).toBeNull();
  for (const [plan, label] of [["Esencial", "US$12.00 al mes"], ["Estándar", "US$19.00 al mes"], ["Pro", "US$35.00 al mes"]])
    expect(within(screen.getByRole("heading", { name: plan }).closest(".card") as HTMLElement).getByText(label)).toBeTruthy();
  for (const [addOn, label] of [["Caja adicional", "US$6.00 al mes"], ["Cajero adicional", "US$2.00 al mes"], ["Sucursal adicional", "US$25.00 al mes"], ["Lectura de facturas", "US$7.00 al mes"], ["Asistente +1,000 preguntas", "US$5.00 al mes"], ["Usuarios del panel +5", "US$3.00 al mes"]])
    expect(within(screen.getByText(addOn).closest("tr")!).getByText(label)).toBeTruthy();
  expect(screen.getByText(/Precios en US\$, provisionales/)).toBeTruthy();
  const accompaniment = screen.getByText("Acompañamiento en la certificación e-CF").closest("tr")!;
  expect(within(accompaniment).getByText("Por confirmar")).toBeTruthy(); // no price yet  expect(screen.getByRole("link", { name: "Crear su cuenta y elegir plan" }).getAttribute("href")).toBe("/app/billing"); // TK-249
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
});

it("TK-245: the home page shows the product: deadline banner, trial and contact calls to action, decorative art and no price placeholder", () => {
  renderAt("/");
  expect(screen.getByRole("heading", { level: 1, name: "Venda, pese y facture. Aunque se vaya el internet." })).toBeTruthy();
  expect(screen.getByRole("link", { name: /15 de noviembre de 2026/ }).getAttribute("href")).toBe("/e-cf");
  const trials = screen.getAllByRole("link", { name: "Probar 14 días gratis" });
  expect(trials.length).toBe(3); // header, hero, price band
  for (const trial of trials) expect(trial.getAttribute("href")).toBe("/descargar");
  expect(screen.getByRole("link", { name: "Hablemos" }).getAttribute("href")).toBe("/precios#contacto"); // no WhatsApp number yet
  expect(document.querySelector(".art__stage")!.getAttribute("aria-hidden")).toBe("true");
  expect(screen.getByText(/cifras son ilustrativas/)).toBeTruthy();
  expect(screen.queryByText("Precio por confirmar")).toBeNull(); // TK-274: no placeholder on the home
  expect(screen.getByRole("heading", { level: 2, name: "Desde US$12.00 al mes" })).toBeTruthy(); // TK-293: the provisional entry price
  expect(screen.getByText("Precios en US$, provisionales.")).toBeTruthy();
  expect(screen.getByRole("link", { name: "¿Necesita certificarse? Le acompañamos →" }).getAttribute("href")).toBe("/certificacion");
  for (const [name, href] of [["Carnicería", "/carniceria"], ["Colmado", "/colmado"], ["Deli", "/deli"]])
    expect(screen.getAllByRole("link", { name: new RegExp(`^${name}`) }).some((a) => a.getAttribute("href") === href)).toBe(true);
});

it.each(["/carniceria", "/colmado", "/deli"])("TK-245: %s keeps both calls to action", (path) => {
  renderAt(path);
  expect(screen.getAllByRole("link", { name: "Probar 14 días gratis" }).length).toBeGreaterThan(1);
  expect(screen.getByRole("link", { name: "Hablemos" })).toBeTruthy();
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
  expect(slides.map((s) => s.getAttribute("aria-label"))[0]).toBe("1 de 8: Costo");
  expect(slides.length).toBe(8);
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

it("TK-296: the home's video never loads or plays by itself, has a descriptions track and a visible transcript from the same script", () => {
  renderAt("/");
  const video = screen.getByLabelText(/Video de 42 segundos/) as HTMLVideoElement;
  expect(video.getAttribute("preload")).toBe("none");
  expect(video.hasAttribute("autoplay")).toBe(false);
  expect(video.querySelector("track")?.getAttribute("kind")).toBe("descriptions");
  expect(video.querySelector("track")?.getAttribute("srclang")).toBe("es");
  const transcript = document.getElementById(video.getAttribute("aria-describedby")!)!;
  expect(within(transcript).getByText("Lo que muestra el video (sin sonido)")).toBeTruthy();
  expect(within(transcript).getByText("Pregúntele a Talia.")).toBeTruthy();
  expect(screen.getByRole("button", { name: "Ver el video (0:42)" })).toBeTruthy();
});
