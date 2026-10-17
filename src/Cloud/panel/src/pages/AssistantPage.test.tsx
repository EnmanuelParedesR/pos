import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen } from "@testing-library/react";
import { Panel } from "../Panel";
import panelSource from "../Panel.tsx?raw";
import type { Me } from "../api";
import { json, renderPage } from "../testing";
import assistantRoutes from "./assistantRoutes.json";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  localStorage.clear();
});

const owner: Me = { email: "dueno@example.com", tenantId: "t0", tenantName: "Carnicería La Esquina", role: "Owner", tenants: [{ tenantId: "t0", name: "Carnicería La Esquina", role: "Owner" }] };

function askWith(navigation: unknown) {
  localStorage.setItem("abastalia.refreshToken", "saved");
  const fetch = vi.fn(async (url: string) =>
    url.endsWith("/api/me") ? json(200, owner) : url.endsWith("/api/assistant/ask") ? json(200, { answer: "Las ventas por día están en Ventas.", navigation }) : json(200, []),
  );
  vi.stubGlobal("fetch", fetch);
  renderPage(<Panel />, { path: "/app/assistant", route: "app/*" });
  return fetch;
}

it("TK-339: Talia's suggested screen is an \"Ir a\" link the user clicks; nothing navigates on its own", async () => {
  const fetch = askWith("/app/sales");
  fireEvent.click(await screen.findByRole("button", { name: "¿Cuánto vendí hoy?" }));

  const go = await screen.findByRole("link", { name: "Ir a Ventas" });
  expect(go.getAttribute("href")).toBe("/app/sales"); // a real link: Tab reaches it and Enter opens it
  expect(screen.getByRole("heading", { name: "Pregúntale a Talia" })).toBeTruthy(); // still on the chat

  // The next question's history carries only role and content back to the server.
  fireEvent.change(screen.getByLabelText("Su pregunta"), { target: { value: "¿Y ayer?" } });
  fireEvent.click(screen.getByRole("button", { name: "Preguntar" }));
  await vi.waitFor(() => expect(fetch.mock.calls.filter(([url]) => String(url).endsWith("/api/assistant/ask"))).toHaveLength(2));
  const body = JSON.parse(String((fetch.mock.calls.filter(([url]) => String(url).endsWith("/api/assistant/ask"))[1] as unknown as [string, RequestInit])[1].body));
  expect(body.history[1]).toEqual({ role: "assistant", content: "Las ventas por día están en Ventas." });

  fireEvent.click(screen.getAllByRole("link", { name: "Ir a Ventas" })[0]);
  expect(await screen.findByRole("heading", { name: "Ventas" })).toBeTruthy();
});

it("TK-339: a route outside the shared list gets no button", async () => {
  askWith("https://evil.example/app");
  fireEvent.click(await screen.findByRole("button", { name: "¿Cuánto vendí hoy?" }));
  expect(await screen.findByText("Las ventas por día están en Ventas.")).toBeTruthy();
  expect(screen.queryByRole("link", { name: /^Ir a/ })).toBeNull();
});

// Panel routes Talia never offers: the chat itself, a screen that needs an id, and Abastalia staff's pages.
const notOffered = (route: string) => route === "/app/assistant" || route.includes(":") || route.startsWith("/app/platform");

it("TK-339: assistantRoutes.json lists every route of Panel.tsx (a route added or removed there fails here)", () => {
  const panel = [...panelSource.matchAll(/<Route path="([^"*]+)"/g)].map((m) => `/app/${m[1]}`);
  expect(panel.length).toBeGreaterThan(5);
  expect(panelSource).toContain("<Route index ");
  const offered = ["/app", ...panel.filter((r) => !notOffered(r))].sort();
  expect(assistantRoutes.map((r) => r.route).filter((r) => r.startsWith("/app")).sort()).toEqual(offered);
  // The Owner-only routes there are the ones a ReadOnly member is never offered.
  const ownerOnly = [...panelSource.matchAll(/me\.role === "Owner" && <Route path="([^"]+)"/g)].map((m) => `/app/${m[1]}`);
  expect(assistantRoutes.filter((r) => "ownerOnly" in r && r.ownerOnly).map((r) => r.route)).toEqual(ownerOnly);
});
