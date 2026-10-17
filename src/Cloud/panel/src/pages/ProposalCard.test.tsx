import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen } from "@testing-library/react";
import { json, renderPage } from "../testing";
import { AssistantChat } from "./AssistantPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

const summary = "Aviso por correo «Comprobantes rechazados por la DGII»: encendido → apagado";
const inMinutes = (m: number) => new Date(Date.now() + m * 60_000).toISOString();

/** The chat with one answer that carries a proposal; `action` answers confirm and cancel. */
function chat(action: (url: string) => Response, expiresAt = inMinutes(10)) {
  const fetch = vi.fn(async (url: string, _init?: RequestInit) =>
    url.endsWith("/api/assistant/ask")
      ? json(200, { answer: "Le preparé el cambio. Confírmelo abajo.", navigation: null, proposal: { id: "p1", summary, expiresAt } })
      : action(url),
  );
  vi.stubGlobal("fetch", fetch);
  renderPage(<AssistantChat />);
  return fetch;
}

const actionCalls = (fetch: ReturnType<typeof chat>) => fetch.mock.calls.filter(([url]) => String(url).includes("/api/assistant/actions/"));
const problem = (status: number, code: string, detail: string) => json(status, { code, detail });
const done = (extra = {}) => json(200, { id: "p1", type: "alert_preference", summary, status: "Done", result: { alert: "DgiiRejected", on: false }, ...extra });

async function ask() {
  fireEvent.click(await screen.findByRole("button", { name: "¿Cuánto vendí hoy?" }));
  return screen.findByRole("region", { name: /^(Talia propone|Propuesta de Talia)$/ });
}

it("TK-340: the card shows the server's summary, the time left and real Confirmar / Cancelar buttons", async () => {
  chat(() => done());
  const card = await ask();
  expect(card.textContent).toContain(summary);
  expect(card.textContent).toContain("Vence en 10 min");
  expect(screen.getByRole("heading", { name: "Talia propone" })).toBeTruthy();
  expect(screen.getByRole("button", { name: "Confirmar" })).toBeTruthy();
  expect(screen.getByRole("button", { name: "Cancelar" })).toBeTruthy();
  expect(screen.getByRole("status").textContent).toBe("Nada cambia hasta que usted confirme.");
});

it("TK-340: Confirmar posts once, even on a double click, and shows Hecho with the focus on the outcome", async () => {
  let answer!: (r: Response) => void;
  const fetch = chat(() => new Promise<Response>((r) => (answer = r)) as unknown as Response);
  await ask();
  const confirm = screen.getByRole("button", { name: "Confirmar" });
  fireEvent.click(confirm);
  fireEvent.click(confirm);
  expect(actionCalls(fetch)).toHaveLength(1);
  expect(String(actionCalls(fetch)[0][0])).toMatch(/\/api\/assistant\/actions\/p1\/confirm$/);
  expect((confirm as HTMLButtonElement).disabled).toBe(true); // running
  expect((screen.getByRole("button", { name: "Cancelar" }) as HTMLButtonElement).disabled).toBe(true);
  expect(screen.getByRole("status").textContent).toBe("Haciéndolo…");

  answer(done());
  await vi.waitFor(() => expect(screen.getByRole("status").textContent).toBe("Hecho."));
  expect(screen.queryByRole("button", { name: "Confirmar" })).toBeNull();
  expect(document.activeElement).toBe(screen.getByRole("status"));
  expect(screen.getByRole("heading", { name: "Propuesta de Talia" })).toBeTruthy();
  expect(actionCalls(fetch)).toHaveLength(1);
});

it("TK-340: a text result (a connection code) is shown on the card", async () => {
  chat(() => done({ result: "ABCD-1234" }));
  await ask();
  fireEvent.click(screen.getByRole("button", { name: "Confirmar" }));
  await vi.waitFor(() => expect(screen.getByRole("status").textContent).toBe("Hecho. ABCD-1234"));
});

it("TK-340: Cancelar posts cancel and closes the card", async () => {
  const fetch = chat(() => json(200, { id: "p1", status: "Cancelled" }));
  await ask();
  fireEvent.click(screen.getByRole("button", { name: "Cancelar" }));
  await vi.waitFor(() => expect(screen.getByRole("status").textContent).toBe("Cancelada. No se cambió nada."));
  expect(String(actionCalls(fetch)[0][0])).toMatch(/\/p1\/cancel$/);
  expect(screen.queryByRole("button", { name: "Confirmar" })).toBeNull();
});

it.each([
  ["Failed (the executor refused)", () => json(200, { id: "p1", status: "Failed", error: "Ese aviso ya está apagado." }), "No se hizo: Ese aviso ya está apagado."],
  ["409 ProposalExpired", () => problem(409, "Assistant.ProposalExpired", "La propuesta venció."), "Esta propuesta venció; pídala de nuevo."],
  ["409 ProposalCancelled", () => problem(409, "Assistant.ProposalCancelled", "Esa propuesta se canceló."), "Cancelada. No se cambió nada."],
  ["409 ProposalRunning", () => problem(409, "Assistant.ProposalRunning", "Esa propuesta ya se está haciendo."), "Esa propuesta ya se está haciendo."],
  ["409 ProposalClosed", () => problem(409, "Assistant.ProposalClosed", "Esa propuesta ya se confirmó."), "Esa propuesta ya se confirmó."],
  ["409 UnknownAction", () => problem(409, "Assistant.UnknownAction", "Esa acción ya no existe."), "No se hizo: Esa acción ya no existe."],
  ["404 ProposalNotFound", () => problem(404, "Assistant.ProposalNotFound", "No encontramos esa propuesta."), "No encontramos esa propuesta."],
  ["403 NotAllowed", () => problem(403, "Assistant.NotAllowed", "Usted ya no tiene permiso para hacer esto."), "Usted ya no tiene permiso para hacer esto. Pídaselo al dueño del negocio."],
  ["403 ActionsOff", () => problem(403, "Assistant.ActionsOff", "Las acciones de Talia no están incluidas en su plan."), "Su plan no incluye que Talia haga cambios. Ella puede seguir contestando y llevándole a la pantalla donde se hace."],
])("TK-340: %s shows its message and closes the card", async (_, response, message) => {
  chat(response);
  await ask();
  fireEvent.click(screen.getByRole("button", { name: "Confirmar" }));
  await vi.waitFor(() => expect(screen.getByRole("status").textContent).toBe(message));
  expect(screen.queryByRole("button", { name: "Confirmar" })).toBeNull();
});

it("TK-340: a retryable problem (too many clicks, offline) keeps the card open to try again", async () => {
  const fetch = chat(() => problem(429, "Assistant.RateLimited", "Demasiados intentos. Espere un minuto."));
  await ask();
  fireEvent.click(screen.getByRole("button", { name: "Confirmar" }));
  await vi.waitFor(() => expect(screen.getByRole("status").textContent).toBe("Demasiados intentos. Espere un minuto."));
  expect(document.activeElement).toBe(screen.getByRole("button", { name: "Confirmar" })); // the keyboard is back where it can retry
  fireEvent.click(screen.getByRole("button", { name: "Confirmar" }));
  await vi.waitFor(() => expect(actionCalls(fetch)).toHaveLength(2));
});

it("TK-340: a proposal past its time shows as expired with no buttons", async () => {
  chat(() => done(), inMinutes(-1));
  await ask();
  expect(screen.getByRole("status").textContent).toBe("Esta propuesta venció; pídala de nuevo.");
  expect(screen.queryByRole("button", { name: "Confirmar" })).toBeNull();
});

it("TK-340: the history sent with the next question carries only role and text, never the card or its result", async () => {
  const fetch = chat(() => done({ result: "ABCD-1234" }));
  await ask();
  fireEvent.click(screen.getByRole("button", { name: "Confirmar" }));
  await vi.waitFor(() => expect(screen.getByRole("status").textContent).toContain("Hecho."));
  fireEvent.change(screen.getByLabelText("Su pregunta"), { target: { value: "Gracias" } });
  fireEvent.click(screen.getByRole("button", { name: "Preguntar" }));
  const asks = () => fetch.mock.calls.filter(([url]) => String(url).endsWith("/api/assistant/ask"));
  await vi.waitFor(() => expect(asks()).toHaveLength(2));
  const sent = String((asks()[1][1] as RequestInit).body);
  expect(JSON.parse(sent).history).toEqual([
    { role: "user", content: "¿Cuánto vendí hoy?" },
    { role: "assistant", content: "Le preparé el cambio. Confírmelo abajo." },
  ]);
  expect(sent).not.toContain("p1");
  expect(sent).not.toContain("ABCD-1234");
  expect(sent).not.toContain("Aviso por correo");
});
