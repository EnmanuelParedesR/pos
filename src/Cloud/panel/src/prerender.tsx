// Build-time entry (TK-228): scripts/prerender.mjs renders each public page to HTML so search engines read it without running JavaScript.
// TK-259/TK-260: the same entry builds each page's <head> tags, its JSON-LD and sitemap.xml.
import { renderToString } from "react-dom/server";
import { StaticRouter } from "react-router";
import { App, movedToApp } from "./App";
import { site } from "./pages/site/config";
import { notFound, sitePages } from "./pages/site/SitePages";

export { movedToApp, site };
export const pages = [...sitePages, { ...notFound, path: "/404" }].map(({ path, title, description, nav }) => ({ path, title, description, nav }));
type Page = (typeof pages)[number];

export const render = (path: string) =>
  renderToString(
    <StaticRouter location={path}>
      <App />
    </StaticRouter>,
  );

/** The pages search engines should list: every public page but the 404. */
export const indexed = (page: Page) => page.path !== "/404";
export const canonical = (path: string) => `${site.url}${path}`;
/** 1200×630; the design system supplies the file (TK-259). */
export const ogImage = `${site.url}/og/abastalia.png`;

const escape = (s: string) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");

/** Title, description, canonical, Open Graph and Twitter card for one page; the 404 gets noindex instead of a canonical. */
export function head(page: Page) {
  const tags = [
    `<title>${escape(page.title)}</title>`,
    `<meta name="description" content="${escape(page.description)}" />`,
    `<meta property="og:type" content="website" />`,
    `<meta property="og:site_name" content="Abastalia" />`,
    `<meta property="og:locale" content="es_DO" />`,
    `<meta property="og:title" content="${escape(page.title)}" />`,
    `<meta property="og:description" content="${escape(page.description)}" />`,
    `<meta property="og:image" content="${ogImage}" />`,
    `<meta property="og:image:width" content="1200" />`,
    `<meta property="og:image:height" content="630" />`,
    `<meta name="twitter:card" content="summary_large_image" />`,
    ...(indexed(page)
      ? [`<link rel="canonical" href="${canonical(page.path)}" />`, `<meta property="og:url" content="${canonical(page.path)}" />`]
      : [`<meta name="robots" content="noindex" />`]),
  ];
  return tags.join("\n    ");
}

const text = (html: string) =>
  html
    .replace(/<[^>]*>/g, "")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#x27;/g, "'")
    .replace(/&amp;/g, "&")
    .trim();

/** Schema.org data for one page, from what the rendered page already shows (`html` is render(page.path)). Organization and the app on the
 * home page, the breadcrumb on the others, FAQPage where the page has "Preguntas frecuentes". No offers: prices come live from Paddle. */
export function jsonLd(page: Page, html: string): object[] {
  if (!indexed(page)) return [];
  const context = "https://schema.org";
  const blocks: object[] = [];
  if (page.path === "/") {
    blocks.push({ "@context": context, "@type": "Organization", name: "Abastalia", url: canonical("/"), logo: `${site.url}/og/logo.png` });
    blocks.push({
      "@context": context,
      "@type": "SoftwareApplication",
      name: "Abastalia",
      url: canonical("/"),
      description: page.description,
      applicationCategory: "BusinessApplication",
      operatingSystem: "Windows",
    });
  } else {
    blocks.push({
      "@context": context,
      "@type": "BreadcrumbList",
      itemListElement: [
        { "@type": "ListItem", position: 1, name: "Inicio", item: canonical("/") },
        { "@type": "ListItem", position: 2, name: page.nav ?? page.title, item: canonical(page.path) },
      ],
    });
  }
  const faq = [...html.matchAll(/<details><summary>(.*?)<\/summary><p>(.*?)<\/p><\/details>/g)];
  if (faq.length)
    blocks.push({
      "@context": context,
      "@type": "FAQPage",
      mainEntity: faq.map(([, q, a]) => ({ "@type": "Question", name: text(q), acceptedAnswer: { "@type": "Answer", text: text(a) } })),
    });
  return blocks;
}

/** <script> tags for the blocks; "<" is escaped so no text can close the tag. */
export const jsonLdTags = (blocks: object[]) =>
  blocks.map((b) => `<script type="application/ld+json">${JSON.stringify(b).replace(/</g, "\\u003c")}</script>`).join("\n    ");

/** sitemap.xml with every indexed page; `lastmod` is the build date (YYYY-MM-DD). */
export const sitemap = (lastmod: string) =>
  [
    `<?xml version="1.0" encoding="UTF-8"?>`,
    `<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">`,
    ...pages.filter(indexed).map((p) => `  <url><loc>${canonical(p.path)}</loc><lastmod>${lastmod}</lastmod></url>`),
    `</urlset>`,
    ``,
  ].join("\n");
