import { describe, expect, it } from "vitest";
import { movedToApp, pages } from "../src/prerender";
import base from "../public/staticwebapp.config.json";
import { swaConfig } from "./swa-config.mjs";

const config = swaConfig(base, pages, movedToApp);

/** Static Web Apps matching: first route wins; "/x/*" matches "/x/…", anything else is exact (case-insensitive). */
const match = (path) =>
  config.routes.find(({ route }) =>
    route.endsWith("/*") ? path.toLowerCase().startsWith(route.slice(0, -1).toLowerCase()) : path.toLowerCase() === route.toLowerCase(),
  );
const servedBy = (path) => match(path)?.rewrite ?? null;

describe("staticwebapp.config.json (TK-228)", () => {
  it("has no fallback, so unknown addresses answer 404 with the prerendered page", () => {
    expect(config.navigationFallback).toBeUndefined();
    expect(config.responseOverrides).toEqual({ 404: { rewrite: "/404.html", statusCode: 404 } });
  });

  it("serves each public page from its prerendered file", () => {
    expect(servedBy("/precios")).toBe("/precios.html");
    expect(servedBy("/e-cf")).toBe("/e-cf.html");
    for (const p of ["/guia-e-cf", "/comparar", "/glosario"]) expect(servedBy(p), p).toBe(`${p}.html`); // TK-263
    expect(servedBy("/404")).toBeNull();
  });

  it("sends the app's own addresses to the empty app", () => {
    for (const path of ["/app", "/app/sales", "/app/restablecer", "/c/abc", "/invite", "/invite/tok", "/unsubscribe", "/shifts/12", "/platform/tenants/x"])
      expect(servedBy(path), path).toBe("/shell.html");
    for (const p of movedToApp) expect(servedBy(`/${p}`), p).toBe("/shell.html");
  });

  it("leaves anything else to the 404", () => {
    for (const path of ["/nada", "/precios/x", "/c", "/application", "/salesman", "/invitex"]) expect(servedBy(path), path).toBeNull();
  });

  it("R11: sends a strict CSP that allows only this site, the API and Paddle's checkout, HSTS and no framing", () => {
    const headers = swaConfig(base, pages, movedToApp, "https://api.abastalia.example/").globalHeaders;
    const csp = Object.fromEntries(headers["Content-Security-Policy"].split("; ").map((d) => [d.split(" ")[0], d.split(" ").slice(1).join(" ")]));
    expect(csp["script-src"]).toBe("'self' https://cdn.paddle.com");
    expect(csp["connect-src"]).toBe("'self' https://api.abastalia.example https://*.paddle.com");
    expect(csp["img-src"]).toBe("'self' data: https://api.abastalia.example https://*.paddle.com"); // R12 #12: the catalog logo
    expect(csp["frame-src"]).toBe("https://buy.paddle.com https://sandbox-buy.paddle.com");
    expect(csp["frame-ancestors"]).toBe("'none'");
    expect(csp["default-src"]).toBe("'self'");
    expect(headers["Strict-Transport-Security"]).toMatch(/^max-age=\d{8,}/);
    expect(headers["X-Frame-Options"]).toBe("DENY");
    expect(headers["X-Content-Type-Options"]).toBe("nosniff"); // the base headers stay
  });

  it("follows the Static Web Apps route rules", () => {
    const routes = config.routes.map((r) => r.route);
    expect(new Set(routes).size).toBe(routes.length);
    expect(routes.length).toBeLessThanOrEqual(100);
    for (const r of routes) expect(r, r).toMatch(/^\/[a-z0-9-]+(\/[a-z0-9-]+)*(\/\*)?$/);
    for (const r of config.routes) expect(Object.keys(r).filter((k) => k === "rewrite" || k === "redirect").length).toBeLessThanOrEqual(1);
    expect(JSON.stringify(config).length).toBeLessThan(20_000);
  });
});
