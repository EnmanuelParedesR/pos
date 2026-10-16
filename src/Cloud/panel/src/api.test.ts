import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { json } from "./testing";

const authHeader = (init?: RequestInit) => (init?.headers as Record<string, string> | undefined)?.Authorization;

// Each test gets a fresh module: the access token lives in module memory.
const load = async () => {
  vi.resetModules();
  return import("./api");
};

describe("api", () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("renews the token once on a 401 and retries with the new one", async () => {
    localStorage.setItem("toki.refreshToken", "old-refresh");
    const fetch = vi.fn(async (url: string, init?: RequestInit) => {
      if (url.endsWith("/api/auth/refresh")) {
        expect(JSON.parse(String(init?.body))).toEqual({ refreshToken: "old-refresh" });
        return json(200, { tokenType: "Bearer", accessToken: "new-access", expiresIn: 3600, refreshToken: "new-refresh" });
      }
      return authHeader(init) === "Bearer new-access" ? json(200, { tenantName: "Carnicería Central" }) : json(401);
    });
    vi.stubGlobal("fetch", fetch);
    const { api } = await load();

    await expect(api("/api/me")).resolves.toEqual({ tenantName: "Carnicería Central" });
    expect(fetch).toHaveBeenCalledTimes(3);
    expect(localStorage.getItem("toki.refreshToken")).toBe("new-refresh");
  });

  it("parallel 401s share one renewal", async () => {
    localStorage.setItem("toki.refreshToken", "r");
    let refreshes = 0;
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string, init?: RequestInit) => {
        if (url.endsWith("/api/auth/refresh")) {
          refreshes++;
          return json(200, { accessToken: "a", refreshToken: "r2" });
        }
        return authHeader(init) ? json(200, []) : json(401);
      }),
    );
    const { api } = await load();

    await Promise.all([api("/api/stores"), api("/api/panel/summary"), api("/api/me")]);
    expect(refreshes).toBe(1);
  });

  it("signs out when the token cannot be renewed", async () => {
    localStorage.setItem("toki.refreshToken", "expired");
    vi.stubGlobal("fetch", vi.fn(async () => json(401)));
    const { api, setOnSignedOut, hasSession } = await load();
    const signedOut = vi.fn();
    setOnSignedOut(signedOut);

    await expect(api("/api/me")).rejects.toMatchObject({ status: 401 });
    expect(signedOut).toHaveBeenCalledOnce();
    expect(hasSession()).toBe(false);
  });

  it("keeps the session when the renewal cannot reach the server", async () => {
    localStorage.setItem("toki.refreshToken", "still-good");
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        if (url.endsWith("/api/auth/refresh")) throw new TypeError("Failed to fetch");
        return json(401);
      }),
    );
    const { api, setOnSignedOut } = await load();
    const signedOut = vi.fn();
    setOnSignedOut(signedOut);

    await expect(api("/api/me")).rejects.toMatchObject({ offline: true });
    expect(signedOut).not.toHaveBeenCalled();
    expect(localStorage.getItem("toki.refreshToken")).toBe("still-good");
  });

  it("reads the code and the Spanish detail from ProblemDetails", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () =>
        json(503, { title: "Assistant.NotConfigured", code: "Assistant.NotConfigured", detail: "El asistente todavía no está configurado.", status: 503 }),
      ),
    );
    const { api } = await load();

    await expect(api("/api/assistant/ask", "POST", { question: "hola", history: [] })).rejects.toMatchObject({
      status: 503,
      code: "Assistant.NotConfigured",
      detail: "El asistente todavía no está configurado.",
    });
  });

  it("an unreachable server is an offline error, not a crash", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => {
        throw new TypeError("Failed to fetch");
      }),
    );
    const { api } = await load();

    await expect(api("/api/stores")).rejects.toMatchObject({ status: 0, offline: true });
  });

  it("shows the register errors from Identity in Spanish, once each", async () => {
    const { ApiError, identityMessages } = await load();
    const error = new ApiError(400, "400", "x", { PasswordTooShort: ["Passwords must be..."], PasswordRequiresDigit: ["..."], PasswordRequiresUpper: ["..."] });

    expect(identityMessages(error)).toEqual([
      "La contraseña debe tener al menos 8 caracteres.",
      "La contraseña debe tener al menos un número.",
      "La contraseña debe tener al menos una letra mayúscula.",
    ]);
  });
});
