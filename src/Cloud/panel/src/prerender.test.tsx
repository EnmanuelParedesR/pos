import { act } from "react";
import { hydrateRoot } from "react-dom/client";
import { MemoryRouter } from "react-router";
import robots from "../public/robots.txt?raw";
import { describe, expect, it, vi } from "vitest";
import { App } from "./App";
import { json } from "./testing";
import { canonical, head, indexed, jsonLd, jsonLdTags, pages, render, site, sitemap } from "./prerender";

it.each(pages.map((p) => [p.path]))("TK-261: the prerendered %s hydrates without React throwing it away", async (path) => {
  vi.stubGlobal("fetch", vi.fn(async () => json(404)));
  const root = document.body.appendChild(document.createElement("div"));
  root.innerHTML = render(path);
  const errors: unknown[] = [];
  await act(async () => {
    hydrateRoot(
      root,
      <MemoryRouter initialEntries={[path]}>
        <App />
      </MemoryRouter>,
      { onRecoverableError: (e) => errors.push(e) },
    );
  });
  expect(errors).toEqual([]);
  root.remove();
  vi.unstubAllGlobals();
});

const publicPages = pages.filter(indexed);
const len = (s: string) => [...s].length;
const blocksOf = (path: string) => {
  const page = pages.find((p) => p.path === path)!;
  return [...jsonLdTags(jsonLd(page, render(path))).matchAll(/<script type="application\/ld\+json">(.*?)<\/script>/g)].map(([, json]) => JSON.parse(json));
};

describe("TK-259: every public page can be found and previewed", () => {
  it.each(publicPages.map((p) => [p.path, p]))("%s has a title of up to 60 characters, a 140–160 character description and a canonical", (path, page) => {
    expect(len(page.title), page.title).toBeLessThanOrEqual(60);
    expect(len(page.description), page.description).toBeGreaterThanOrEqual(140);
    expect(len(page.description), page.description).toBeLessThanOrEqual(160);
    const tags = head(page);
    expect(tags).toContain(`<link rel="canonical" href="${canonical(path)}" />`);
    expect(tags).toContain(`<meta property="og:url" content="${canonical(path)}" />`);
    expect(tags).toContain(`<meta property="og:image" content="https://abastalia.com/og/abastalia.png" />`);
    expect(tags).toContain(`<meta name="twitter:card" content="summary_large_image" />`);
    expect(tags).not.toContain("noindex");
  });

  it("no two pages share a title or a description", () => {
    expect(new Set(publicPages.map((p) => p.title)).size).toBe(publicPages.length);
    expect(new Set(publicPages.map((p) => p.description)).size).toBe(publicPages.length);
  });

  it("the sitemap lists every public page with its lastmod, and not the 404", () => {
    const xml = sitemap("2026-10-16");
    const locs = [...xml.matchAll(/<url><loc>(.*?)<\/loc><lastmod>2026-10-16<\/lastmod><\/url>/g)].map(([, loc]) => loc);
    expect(locs).toEqual(publicPages.map((p) => canonical(p.path)));
    expect(xml).not.toContain("404");
  });

  it("robots.txt points to the sitemap on the canonical origin; the 404 is noindex", () => {
    expect(site.url).toBe("https://abastalia.com");
    expect(robots).toContain("Sitemap: https://abastalia.com/sitemap.xml");
    const notFound = head(pages.find((p) => p.path === "/404")!);
    expect(notFound).toContain('<meta name="robots" content="noindex" />');
    expect(notFound).not.toContain("canonical");
  });
});

describe("TK-260: JSON-LD", () => {
  it.each(publicPages.map((p) => [p.path]))("%s: each block parses and has @context and @type", (path) => {
    const blocks = blocksOf(path);
    expect(blocks.length).toBeGreaterThan(0);
    for (const b of blocks) {
      expect(b["@context"]).toBe("https://schema.org");
      expect(typeof b["@type"]).toBe("string");
    }
  });

  it("the home page has the organization and the app, without invented prices", () => {
    const [org, app] = blocksOf("/");
    expect(org).toMatchObject({ "@type": "Organization", name: "Abastalia", url: "https://abastalia.com/", logo: "https://abastalia.com/og/logo.png" });
    expect(app).toMatchObject({ "@type": "SoftwareApplication", applicationCategory: "BusinessApplication", operatingSystem: "Windows" });
    expect(app.offers).toBeUndefined();
  });

  it("subpages have a breadcrumb; FAQPage repeats the questions the page shows, as plain text", () => {
    expect(blocksOf("/precios").map((b) => b["@type"])).toEqual(["BreadcrumbList"]);
    const [crumbs, faq] = blocksOf("/e-cf");
    expect(crumbs.itemListElement.map((i: { name: string; item: string }) => [i.name, i.item])).toEqual([
      ["Inicio", "https://abastalia.com/"],
      ["e-CF", "https://abastalia.com/e-cf"],
    ]);
    expect(faq["@type"]).toBe("FAQPage");
    expect(faq.mainEntity[2]).toEqual({
      "@type": "Question",
      name: "¿Cuánto tarda la certificación?",
      acceptedAnswer: {
        "@type": "Answer",
        text: "Depende en buena parte de la DGII. La parte técnica (set de pruebas y simulación) la hace Abastalia. Vea Certifíquese con Abastalia.",
      },
    });
    for (const path of ["/carniceria", "/colmado", "/deli", "/guia-e-cf", "/comparar", "/glosario"]) expect(blocksOf(path).map((b) => b["@type"]), path).toContain("FAQPage");
  });
});
