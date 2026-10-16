// TK-228: after `vite build` and `vite build --ssr src/prerender.tsx`, writes each public page as real HTML (title, description, content) so
// search engines and link previews read it without JavaScript. The app then takes over in the browser.
//   dist/index.html, dist/precios.html, …  the public pages
//   dist/shell.html                        the empty app, for every other address (/app, /c/…, /invite)
import { readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "dist");
const ssr = join(root, ".ssr");
const { render, pages, site } = await import(pathToFileURL(join(ssr, "prerender.js")).href);

const shell = readFileSync(join(dist, "index.html"), "utf8");
writeFileSync(join(dist, "shell.html"), shell);

const escape = (s) => s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/"/g, "&quot;");
const file = (path) => (path === "/" ? "index.html" : `${path.slice(1)}.html`);

for (const page of pages) {
  const url = site.url && page.path !== "/404" ? `${site.url}${page.path === "/" ? "/" : page.path}` : "";
  const head = [
    `<title>${escape(page.title)}</title>`,
    `<meta name="description" content="${escape(page.description)}" />`,
    `<meta property="og:type" content="website" />`,
    `<meta property="og:locale" content="es_DO" />`,
    `<meta property="og:title" content="${escape(page.title)}" />`,
    `<meta property="og:description" content="${escape(page.description)}" />`,
    // shortcut: no domain yet; canonical and og:url appear once config.ts has `url`.
    ...(url ? [`<link rel="canonical" href="${url}" />`, `<meta property="og:url" content="${url}" />`] : []),
  ].join("\n    ");
  const html = shell
    .replace(/<title>.*?<\/title>/, head)
    .replace('<div id="root"></div>', `<div id="root">${render(page.path)}</div>`);
  if (html === shell) throw new Error(`index.html changed shape; cannot prerender ${page.path}`);
  writeFileSync(join(dist, file(page.path)), html);
}

// Static Web Apps: each public path serves its page; every other address falls back to the app.
const configPath = join(dist, "staticwebapp.config.json");
const config = JSON.parse(readFileSync(configPath, "utf8"));
config.routes = [
  ...pages.filter((p) => p.path !== "/" && p.path !== "/404").map((p) => ({ route: p.path, rewrite: `/${file(p.path)}` })),
  ...(config.routes ?? []),
];
config.navigationFallback.rewrite = "/shell.html";
config.responseOverrides = { 404: { rewrite: "/404.html", statusCode: 404 } };
writeFileSync(configPath, JSON.stringify(config, null, 2));

rmSync(ssr, { recursive: true, force: true });
console.log(`Prerendered ${pages.length} public pages`);
