import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { Panel } from "./Panel";
import type { Me } from "./api";
import { json, renderPage } from "./testing";
import { withLinks } from "./pages/AssistantPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const owner: Me = { email: "dueno@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "Owner", tenants: [{ tenantId: "t0", name: "Carnicería La Esquina", role: "Owner" }] };

function renderApp(path: string) {
  localStorage.setItem("abastalia.refreshToken", "saved");
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    if (url.endsWith("/api/me")) return json(200, owner);
    if (url.endsWith("/api/assistant/ask")) return json(200, { answer: `Respuesta a: ${JSON.parse(String(init?.body)).question}` });
    return json(200, []);
  });
  vi.stubGlobal("fetch", fetch);
  renderPage(<Panel />, { path, route: "app/*" });
  return fetch;
}

const tabbar = async () => {
  const navs = await screen.findAllByRole("navigation", { name: "Secciones" });
  return navs.find((n) => n.classList.contains("esc-tabbar"))!;
};

it("TK-299: the phone's bottom bar shows four sections and 'Más' with the rest", async () => {
  renderApp("/app/sales");
  const bar = await tabbar();

  expect(within(bar).getAllByRole("link").map((a) => a.textContent)).toEqual(["Resumen", "Ventas", "Cuadres", "Tiendas"]);
  const more = within(bar).getByRole("button", { name: "Más" });
  expect(more.getAttribute("aria-expanded")).toBe("false");

  fireEvent.click(more);
  expect(more.getAttribute("aria-expanded")).toBe("true");
  expect(within(bar).getByRole("link", { name: "Facturación" })).toBeTruthy();
  expect(within(bar).getByRole("link", { name: "Asistente" })).toBeTruthy();

  fireEvent.keyDown(document, { key: "Escape" });
  expect(within(bar).queryByRole("link", { name: "Facturación" })).toBeNull();

  fireEvent.click(more);
  fireEvent.click(within(bar).getByRole("link", { name: "Equipo" }));
  await vi.waitFor(() => expect(within(bar).queryByRole("link", { name: "Facturación" })).toBeNull());
  expect(more.className).toContain("active");
});

it("TK-300: Talia's button opens the chat as a dialog; Esc closes it and the conversation stays", async () => {
  const fetch = renderApp("/app/sales");
  const fab = await screen.findByRole("button", { name: "Pregúntale a Talia" });
  fab.focus();
  fireEvent.click(fab);

  const dialog = screen.getByRole("dialog", { name: "Talia" });
  expect(dialog.getAttribute("aria-modal")).toBe("true");
  fireEvent.change(within(dialog).getByLabelText("Su pregunta"), { target: { value: "¿Cuánto vendí hoy?" } });
  fireEvent.click(within(dialog).getByRole("button", { name: "Preguntar" }));
  expect(await within(dialog).findByText("Respuesta a: ¿Cuánto vendí hoy?")).toBeTruthy();
  expect(fetch.mock.calls.filter(([url]) => String(url).endsWith("/api/assistant/ask"))).toHaveLength(1);

  fireEvent.keyDown(dialog, { key: "Escape" });
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(document.activeElement).toBe(fab);

  fireEvent.click(fab);
  expect(within(screen.getByRole("dialog")).getByText("Respuesta a: ¿Cuánto vendí hoy?")).toBeTruthy();
});

it("TK-300: Tab stays inside Talia's dialog", async () => {
  renderApp("/app/stores");
  fireEvent.click(await screen.findByRole("button", { name: "Pregúntale a Talia" }));
  const dialog = screen.getByRole("dialog");
  const close = within(dialog).getByRole("button", { name: "Cerrar" });
  const input = within(dialog).getByLabelText("Su pregunta");

  input.focus();
  fireEvent.keyDown(input, { key: "Tab" }); // "Preguntar" is disabled with an empty question: the input is the last stop.
  expect(document.activeElement).toBe(close);
  fireEvent.keyDown(close, { key: "Tab", shiftKey: true });
  expect(document.activeElement).toBe(input);
});

it("TK-300: the 'Pregúntale a Talia' bar asks in the popup, without leaving the screen", async () => {
  renderApp("/app/shifts");
  fireEvent.change(await screen.findByPlaceholderText(/Pregúntale a Talia/), { target: { value: "¿Quién tuvo faltante?" } });
  fireEvent.click(screen.getByRole("button", { name: "Preguntar" }));

  const dialog = screen.getByRole("dialog");
  expect(await within(dialog).findByText("Respuesta a: ¿Quién tuvo faltante?")).toBeTruthy();
});

it("TK-300: the Asistente page keeps its own chat and shows no popup button", async () => {
  renderApp("/app/assistant");
  expect(await screen.findByRole("heading", { name: "Pregúntale a Talia" })).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Pregúntale a Talia" })).toBeNull();

  fireEvent.click(screen.getByRole("button", { name: "¿Cuánto vendí hoy?" }));
  expect(await screen.findByText("Respuesta a: ¿Cuánto vendí hoy?")).toBeTruthy();
});

it("TK-301: only known in-app paths in Talia's answer become links; the rest stays plain text", () => {
  render(
    <MemoryRouter>
      <p data-testid="answer">{withLinks("Vea /app/billing. También /precios#contacto o (/e-cf), pero no /admin, https://x.com/app, /appx ni <b>/glosario</b>")}</p>
    </MemoryRouter>,
  );
  expect(screen.getAllByRole("link").map((a) => a.getAttribute("href"))).toEqual(["/app/billing", "/precios#contacto", "/e-cf"]);
  expect(screen.getByTestId("answer").querySelector("b")).toBeNull(); // the model's markup stays literal text
  expect(screen.getByTestId("answer").textContent).toContain("<b>/glosario</b>");
});

it("TK-301: a link in the popup's answer navigates in the same tab", async () => {
  const fetch = renderApp("/app/sales");
  fetch.mockImplementation(async (url: string) =>
    url.endsWith("/api/me") ? json(200, owner) : url.endsWith("/api/assistant/ask") ? json(200, { answer: "Sus tiendas están en /app/stores." }) : json(200, []),
  );
  fireEvent.click(await screen.findByRole("button", { name: "Pregúntale a Talia" }));
  const dialog = screen.getByRole("dialog");
  fireEvent.click(within(dialog).getByRole("button", { name: "¿Cuánto vendí hoy?" }));
  fireEvent.click(await within(dialog).findByRole("link", { name: "/app/stores" }));
  expect(await screen.findByRole("heading", { name: "Tiendas" })).toBeTruthy();
});
