import { afterEach, expect, test, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router";
import { SiteTalia } from "./SiteTalia";
import { json } from "../../testing";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const renderTalia = () =>
  render(
    <MemoryRouter>
      <SiteTalia />
    </MemoryRouter>,
  );

// TK-315: Talia on the site asks the public endpoint, with no token, and turns known pages into links.
test("the site's Talia opens, asks the public endpoint without a session and links the pages she names", async () => {
  const fetch = vi.fn(async (url: string, _init?: RequestInit) =>
    url.endsWith("/api/public/assistant/ask") ? json(200, { answer: "Los planes están en /precios." }) : json(204),
  );
  vi.stubGlobal("fetch", fetch);
  renderTalia();

  fireEvent.click(screen.getByRole("button", { name: "Pregúntale a Talia" }));
  expect(screen.getByRole("dialog", { name: "Talia" })).toBeTruthy();
  fireEvent.click(await screen.findByRole("button", { name: "¿Cuánto cuesta?" }));

  expect((await screen.findByRole("link", { name: "/precios" })).getAttribute("href")).toBe("/precios");
  const [url, init] = fetch.mock.calls[0];
  expect(url).toMatch(/\/api\/public\/assistant\/ask$/);
  expect((init?.headers as Record<string, string>).Authorization).toBeUndefined();
  // The page the visitor is on goes along (jsdom's pathname is "/").
  expect(JSON.parse(String(init?.body))).toEqual({ question: "¿Cuánto cuesta?", history: [], page: "/" });
  // TK-315: the answers say they are automatic, with a way to a person.
  expect(screen.getByText("Asistente automático de Abastalia")).toBeTruthy();
  expect(screen.getByRole("link", { name: "Hablar con una persona" }).getAttribute("href")).toBe("/precios#contacto");
});

// TK-349: rating an answer sends the question and the answer once, and thanks. The buttons stay (the focus is not lost), disabled.
test("rating an answer sends it once and says thanks", async () => {
  const fetch = vi.fn(async (url: string, _init?: RequestInit) => (url.endsWith("/ask") ? json(200, { answer: "Sí, funciona sin internet." }) : json(204)));
  vi.stubGlobal("fetch", fetch);
  renderTalia();

  fireEvent.click(screen.getByRole("button", { name: "Pregúntale a Talia" }));
  fireEvent.click(await screen.findByRole("button", { name: "¿Funciona sin internet?" }));
  fireEvent.click(await screen.findByRole("button", { name: "Me sirvió" }));

  expect(await screen.findByText("Gracias, eso ayuda a mejorar a Talia.")).toBeTruthy();
  const up = screen.getByRole("button", { name: "Me sirvió" });
  const down = screen.getByRole("button", { name: "No me sirvió" });
  expect(up.getAttribute("aria-disabled")).toBe("true");
  expect(up.getAttribute("aria-pressed")).toBe("true");
  expect(down.getAttribute("aria-pressed")).toBe("false");
  fireEvent.click(down); // already rated: nothing more is sent
  await waitFor(() => expect(fetch.mock.calls.some(([u]) => String(u).endsWith("/api/public/assistant/feedback"))).toBe(true));
  const sent = fetch.mock.calls.filter(([u]) => String(u).endsWith("/feedback"));
  expect(sent).toHaveLength(1);
  expect(JSON.parse(String(sent[0][1]?.body))).toEqual({ helpful: true, question: "¿Funciona sin internet?", answer: "Sí, funciona sin internet." });
});

// TK-315: a visitor Talia did not help is offered a person.
test("a thumbs-down on the site offers to talk to a person", async () => {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => (url.endsWith("/ask") ? json(200, { answer: "Sí, funciona sin internet." }) : json(204))));
  renderTalia();

  fireEvent.click(screen.getByRole("button", { name: "Pregúntale a Talia" }));
  fireEvent.click(await screen.findByRole("button", { name: "¿Funciona sin internet?" }));
  fireEvent.click(await screen.findByRole("button", { name: "No me sirvió" }));

  expect(await screen.findByText(/¿Prefiere/)).toBeTruthy();
  expect(screen.queryByText("Gracias, eso ayuda a mejorar a Talia.")).toBeNull();
  expect(screen.getByRole("link", { name: "hablar con una persona" }).getAttribute("href")).toBe("/precios#contacto");
});

// A resting Talia (daily budget) is amber, not an error: it is not the visitor's fault.
test("when Talia is resting the visitor sees why and the question stays in the box", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(429, { code: "Assistant.Resting", detail: "Talia ya contestó muchas preguntas hoy." })));
  renderTalia();

  fireEvent.click(screen.getByRole("button", { name: "Pregúntale a Talia" }));
  fireEvent.click(await screen.findByRole("button", { name: "¿Me sirve para un colmado?" }));

  expect(await screen.findByText("Talia ya contestó muchas preguntas hoy.")).toBeTruthy();
  expect((screen.getByLabelText("Su pregunta") as HTMLInputElement).value).toBe("¿Me sirve para un colmado?");
});
