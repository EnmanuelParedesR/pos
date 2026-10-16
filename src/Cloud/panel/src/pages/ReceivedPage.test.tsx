import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter, Outlet, Route, Routes } from "react-router";
import type { PanelContext } from "../App";
import type { ReceivedEcf } from "../api";
import { json } from "../testing";
import { ReceivedPage } from "./ReceivedPage";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const doc: ReceivedEcf = {
  id: 7,
  environment: "ecf",
  issuerRnc: "101672919",
  issuerName: "Distribuidora del Cibao SRL",
  eNcf: "E310000000005",
  type: 31,
  issuedOn: "2026-10-15",
  total: 1180,
  itbis: 180,
  receivedUtc: "2026-10-15T15:00:00Z",
  approval: null,
  approvalReason: null,
  approvalUtc: null,
  approvalDeliveredUtc: null,
  approvalDgiiUtc: null,
  approvalMessage: null,
  acknowledgedUtc: "2026-10-15T15:00:00Z",
  panelDecision: null,
};

function renderPage(canEdit: boolean) {
  const context: PanelContext = { stores: [], reloadStores: () => {}, storeId: "", setStoreId: () => {}, canEdit };
  render(
    <MemoryRouter>
      <Routes>
        <Route element={<Outlet context={context} />}>
          <Route index element={<ReceivedPage />} />
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

it("TK-250: the owner rejects with a reason and the panel shows it waiting for the store", async () => {
  let current = doc;
  const posts: { url: string; body: unknown }[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith("/decision")) {
        posts.push({ url, body: JSON.parse(String(init?.body)) });
        current = { ...doc, panelDecision: "Rejected", panelReason: "Montos incorrectos", panelDecidedBy: "ana@example.com", panelDecidedUtc: "2026-10-16T12:00:00Z" };
        return json(200, current);
      }
      if (url.endsWith("/api/panel/fe/received")) return json(200, [current]);
      return json(200, { status: { active: true, signingMode: "StoreSigned" }, signatures: [], approvals: [], offline: [] });
    }),
  );
  renderPage(true);

  fireEvent.click(await screen.findByText("Distribuidora del Cibao SRL"));
  fireEvent.click(screen.getByRole("button", { name: "Rechazar" }));
  fireEvent.change(screen.getByLabelText("Motivo del rechazo"), { target: { value: "Montos incorrectos" } });
  fireEvent.click(screen.getByRole("button", { name: "Sí, rechazar" }));

  expect(await screen.findByText("Pendiente: se firmará en la tienda")).toBeTruthy();
  expect(screen.getByText(/ana@example.com/)).toBeTruthy();
  expect(screen.getByText("Rechazado · en espera")).toBeTruthy();
  expect(posts).toHaveLength(1);
  expect(posts[0].url).toMatch("/api/panel/fe/received/7/decision");
  expect(posts[0].body).toEqual({ approve: false, reason: "Montos incorrectos" });
  expect(screen.queryByRole("button", { name: "Aceptar" })).toBeNull();
});

it("TK-250: a read-only member cannot decide, and a decision made first at the store is the one shown", async () => {
  const decided: ReceivedEcf = { ...doc, approval: "Approved", approvalUtc: "2026-10-16T11:00:00Z", panelDecision: "Rejected", panelReason: "Montos incorrectos" };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string) =>
      url.endsWith("/api/panel/fe/received") ? json(200, [doc, { ...decided, id: 8, eNcf: "E310000000006" }]) : json(200, { status: { active: false }, signatures: [], approvals: [] }),
    ),
  );
  renderPage(false);

  fireEvent.click(await screen.findByText("E310000000005"));
  expect(screen.queryByRole("button", { name: "Aceptar" })).toBeNull();
  expect(screen.getByText(/El dueño lo acepta o lo rechaza aquí/)).toBeTruthy();

  fireEvent.click(screen.getByText("E310000000006"));
  expect(screen.getByText("Se decidió primero en la tienda: esa decisión es la que vale.")).toBeTruthy();
});
