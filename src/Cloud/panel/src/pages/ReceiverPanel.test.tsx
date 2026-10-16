import { afterEach, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReceiverPanel as ReceiverData } from "../api";
import { json } from "../testing";
import { ReceiverPanel } from "./ReceiverPanel";

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const active: ReceiverData = {
  status: {
    active: true,
    rnc: "101010101",
    subject: "CN=Carnicería La Esquina",
    notAfterUtc: "2027-10-01T00:00:00Z",
    consentedUtc: "2026-10-15T15:00:00Z",
    ownerName: "Ana Pérez",
    holderName: "Ana Pérez",
    urls: [{ environment: "ecf", reception: "https://api.toki/ecf/101010101/fe/recepcion/api/ecf", approval: "https://api.toki/a", authentication: "https://api.toki/s" }],
  },
  signatures: [{ atUtc: "2026-10-15T16:00:00Z", root: "ARECF", eNcf: "E310000000001", sha256: "abc" }],
  approvals: [],
};

it("TK-241: shows the receiver and withdraws it only after two confirmations", async () => {
  let data = active;
  const posts: string[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      if (init?.method === "POST") {
        posts.push(url);
        data = { status: { ...active.status, active: false }, signatures: [], approvals: [] };
        return json(204);
      }
      return json(200, data);
    }),
  );
  render(<ReceiverPanel canEdit />);

  expect(await screen.findByText("101010101")).toBeTruthy();
  expect(screen.getByText("https://api.toki/ecf/101010101/fe/recepcion/api/ecf")).toBeTruthy();
  expect(screen.getByText(/ARECF/)).toBeTruthy();

  fireEvent.click(screen.getByRole("button", { name: "Retirar" }));
  fireEvent.click(screen.getByRole("button", { name: "Entiendo, continuar" }));
  expect(posts).toEqual([]);
  fireEvent.click(screen.getByRole("button", { name: "Sí, retirar el certificado" }));
  expect(await screen.findByText(/No está activo/)).toBeTruthy();
  expect(posts).toHaveLength(1);
  expect(posts[0]).toMatch("/api/panel/fe/revoke");
});

it("TK-241: a read-only member sees the receiver but cannot withdraw it", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => json(200, active)));
  render(<ReceiverPanel canEdit={false} />);
  expect(await screen.findByText("101010101")).toBeTruthy();
  expect(screen.queryByRole("button", { name: "Retirar" })).toBeNull();
});
