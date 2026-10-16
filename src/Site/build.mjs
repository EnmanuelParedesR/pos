// Toki public site (TK-197, TK-198, TK-226): static pages, no dependencies.
//   node build.mjs           → dist/ (what Azure Static Web Apps serves)
//   node build.mjs --serve   → build, then http://localhost:4173
// Each page in pages/ starts with a JSON comment: <!-- {"title": "...", "description": "...", "nav": "precios"} -->
// The build wraps it in the shared header and footer, copies assets and the Escuadra fonts, and fails on a broken internal link.
import { cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(fileURLToPath(import.meta.url));
const dist = join(root, 'dist');
const site = JSON.parse(readFileSync(join(root, 'site.json'), 'utf8'));

const nav = [
  ['index', 'Inicio'], ['carniceria', 'Carnicería'], ['colmado', 'Colmado'], ['deli', 'Deli'],
  ['e-cf', '¿Ya te toca el e-CF?'], ['certificacion', 'Certifícate'], ['precios', 'Precios'], ['novedades', 'Novedades'],
];

// shortcut: no WhatsApp number yet (owner decision); the call to action points at the contact box on Precios until site.json has one.
const contact = site.whatsapp
  ? `https://wa.me/${site.whatsapp}?text=${encodeURIComponent('Hola, quiero saber más de Toki')}`
  : 'precios.html#contacto';

const contactBox = site.whatsapp
  ? `<p><a class="btn" href="${contact}">Escríbenos por WhatsApp</a></p>`
  : '<p><span class="tbd">Canal de contacto por confirmar</span></p>';

const logo = `<svg aria-hidden="true" width="32" height="32" viewBox="0 0 120 120"><path d="M44 24 H96 Q104 24 104 32 V88 Q104 96 96 96 H44 L16 60 Z" fill="#0B5CAD" stroke="#0B5CAD" stroke-width="6" stroke-linejoin="round"/><circle cx="35" cy="60" r="8" fill="#fff"/><circle cx="32.5" cy="60" r="3.8" fill="#14181D"/><path d="M70 36 V76 Q70 84 78 84 H86 M58 50 H86" fill="none" stroke="#fff" stroke-width="10" stroke-linecap="round" stroke-linejoin="round"/></svg>`;

const escape = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;');

function layout(name, meta, body) {
  const links = nav.map(([page, label]) =>
    `<li><a href="${page === 'index' ? './' : `${page}.html`}"${page === meta.nav || page === name ? ' aria-current="page"' : ''}>${label}</a></li>`).join('');
  // shortcut: no domain yet; canonical and og:url appear once site.json has "url".
  const url = site.url && `${site.url}/${name === 'index' ? '' : `${name}.html`}`;
  return `<!doctype html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<base href="/">
<title>${escape(meta.title)}</title>
<meta name="description" content="${escape(meta.description)}">
<meta name="theme-color" content="#0B5CAD">
${url ? `<link rel="canonical" href="${url}">
<meta property="og:url" content="${url}">
` : ''}<meta property="og:type" content="website">
<meta property="og:locale" content="es_DO">
<meta property="og:title" content="${escape(meta.title)}">
<meta property="og:description" content="${escape(meta.description)}">
<link rel="icon" href="favicon.svg" type="image/svg+xml">
<link rel="preload" href="fonts/IBMPlexSans-Regular-Latin1.woff2" as="font" type="font/woff2" crossorigin>
<link rel="stylesheet" href="site.css">
</head>
<body>
<a class="skip" href="#contenido">Saltar al contenido</a>
<header class="top">
  <div class="wrap top__bar">
    <a class="brand" href="./">${logo}<span>toki</span></a>
    <a class="btn btn--small" href="{{contacto}}">Hablemos</a>
  </div>
  <nav class="wrap menu" aria-label="Principal"><ul>${links}</ul></nav>
</header>
<main id="contenido">
${body.trim()}
</main>
<footer class="foot">
  <div class="wrap foot__grid">
    <p><strong>Toki</strong> · Punto de venta para carnicerías, colmados y delis en República Dominicana. Vende sin internet y emite e-CF.</p>
    <nav aria-label="Pie de página"><ul>${links}</ul></nav>
    <p class="muted">La información sobre la DGII es orientativa y no sustituye la asesoría de tu contador ni lo que publique la DGII.</p>
  </div>
</footer>
</body>
</html>
`;
}

function build() {
  rmSync(dist, { recursive: true, force: true });
  mkdirSync(dist, { recursive: true });
  cpSync(join(root, 'assets'), dist, { recursive: true });
  cpSync(join(root, '..', 'Client', 'Toki.Escuadra', 'wwwroot', 'fonts'), join(dist, 'fonts'), {
    recursive: true, filter: f => !f.endsWith('.md'),
  });

  const pages = readdirSync(join(root, 'pages')).filter(f => f.endsWith('.html'));
  for (const file of pages) {
    const source = readFileSync(join(root, 'pages', file), 'utf8');
    const match = source.match(/^<!--\s*(\{[\s\S]*?\})\s*-->/);
    if (!match) throw new Error(`${file}: falta el comentario con title y description`);
    const meta = JSON.parse(match[1]);
    const html = layout(file.replace('.html', ''), meta, source.slice(match[0].length)).replaceAll('{{contacto}}', contact).replaceAll('{{contacto-caja}}', contactBox);
    writeFileSync(join(dist, file), html);
  }

  // Every local href must exist (anchors are not checked).
  const broken = [];
  for (const file of pages) {
    for (const [, href] of readFileSync(join(dist, file), 'utf8').matchAll(/href="([^"#:]+)(?:#[^"]*)?"/g)) {
      const target = href === './' ? 'index.html' : href;
      if (!existsSync(join(dist, target))) broken.push(`${file} → ${href}`);
    }
  }
  if (broken.length) throw new Error(`Enlaces rotos:\n${broken.join('\n')}`);
  console.log(`Sitio listo: ${pages.length} páginas en ${dist}`);
}

const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2', '.json': 'application/json', '.txt': 'text/plain' };

build();
if (process.argv.includes('--serve')) {
  createServer((req, res) => {
    const path = decodeURIComponent(new URL(req.url, 'http://x').pathname);
    let file = join(dist, path.endsWith('/') ? `${path}index.html` : path);
    const found = file.startsWith(dist) && existsSync(file);
    if (!found) file = join(dist, '404.html');
    res.writeHead(found ? 200 : 404, { 'content-type': types[extname(file)] ?? 'application/octet-stream' });
    res.end(readFileSync(file));
  }).listen(4173, () => console.log('Vista previa: http://localhost:4173'));
}
