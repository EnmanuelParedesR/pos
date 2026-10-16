// TK-228: after `vite build` and `vite build --ssr src/prerender.tsx`, writes each public page as real HTML (title, description, content) so
// search engines and link previews read it without JavaScript. The app then takes over in the browser.
//   dist/index.html, dist/precios.html, …  the public pages, with canonical, Open Graph and JSON-LD (TK-259, TK-260)
//   dist/shell.html                        the empty app, for the app's own addresses (/app, /c/…, /invite); see swa-config.mjs. noindex.
//   dist/sitemap.xml                       every public page (TK-259); robots.txt points to it
import { readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { swaConfig } from "./swa-config.mjs";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const dist = join(root, "dist");
const ssr = join(root, ".ssr");
const { render, pages, movedToApp, head, jsonLd, jsonLdTags, sitemap } = await import(pathToFileURL(join(ssr, "prerender.js")).href);

const shell = readFileSync(join(dist, "index.html"), "utf8");
const noindex = shell.replace(/(<title>.*?<\/title>)/, '$1\n    <meta name="robots" content="noindex" />');
if (noindex === shell) throw new Error("index.html has no <title>; cannot mark shell.html noindex");
writeFileSync(join(dist, "shell.html"), noindex);

const file = (path) => (path === "/" ? "index.html" : `${path.slice(1)}.html`);
// TK-261: the headings (bold) and the text (regular) start loading with the CSS, not after it, so the largest text paints sooner.
const fonts = readdirSync(join(dist, "assets"))
  .filter((f) => /^IBMPlexSans-(Regular|Bold)-.*\.woff2$/.test(f))
  .map((f) => `<link rel="preload" href="/assets/${f}" as="font" type="font/woff2" crossorigin />`);

for (const page of pages) {
  const body = render(page.path);
  const tags = [head(page), ...fonts, jsonLdTags(jsonLd(page, body))].filter(Boolean).join("\n    ");
  const html = shell.replace(/<title>.*?<\/title>/, tags).replace('<div id="root"></div>', `<div id="root">${body}</div>`);
  if (html === shell) throw new Error(`index.html changed shape; cannot prerender ${page.path}`);
  writeFileSync(join(dist, file(page.path)), html);
}

writeFileSync(join(dist, "sitemap.xml"), sitemap(new Date().toISOString().slice(0, 10)));

// Static Web Apps: public pages, the app's own addresses and a real 404 for everything else.
const configPath = join(dist, "staticwebapp.config.json");
// R11 B-Med5: the CSP allows the API the build talks to (same default as src/api.ts).
const apiUrl = process.env.VITE_API_URL || "http://localhost:5100";
writeFileSync(configPath, JSON.stringify(swaConfig(JSON.parse(readFileSync(configPath, "utf8")), pages, movedToApp, apiUrl), null, 2));

rmSync(ssr, { recursive: true, force: true });
console.log(`Prerendered ${pages.length} public pages and sitemap.xml`);
