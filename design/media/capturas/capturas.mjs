// TK-281: real screenshots of the register for the site's step-by-step. Local test store only (fictional data).
import { chromium } from "playwright-core";
import { readFileSync } from "node:fs";

const out = process.argv[2];
const pin = JSON.parse(readFileSync(process.argv[3], "utf8")).ScreenshotStore.adminPin;
const browser = await chromium.launch({ channel: "msedge" });
const ctx = await browser.newContext({ viewport: { width: 1366, height: 768 }, deviceScaleFactor: 1.5, colorScheme: "light", locale: "es-DO" });
const page = await ctx.newPage();
const part = process.argv[4];
const sleep = (ms) => page.waitForTimeout(ms);

// Step 1: the first-run screen on a PC that is not set up yet (second instance with an empty device file). Filled, never submitted.
if (part === "setup") {
await page.goto("http://localhost:5280/");
await page.getByRole("button", { name: "Instalar Abastalia" }).waitFor();
const field = (n) => page.locator("label", { hasText: n }).locator("input").first();
await field("Su nombre").fill("Ana Pérez");
await field("Nombre del negocio").fill("Comercial La Esquina");
await field("Teléfono").fill("809-555-0142");
await field("Dirección").fill("Av. Duarte 120, Santo Domingo");
await sleep(300);
await page.screenshot({ path: `${out}/paso-1-instalar.png` });
await browser.close(); process.exit(0);
}

// Sign in on the configured register.
await page.goto("http://localhost:5280/sign-in");
await page.getByRole("button", { name: /Ana Pérez/ }).first().click();
for (const d of pin) await page.getByRole("button", { name: d, exact: true }).click();
await page.getByRole("button", { name: /^Entrar/ }).click();
await page.waitForURL(/admin|sale/);

// Step 2: products (the sample catalog).
await page.evaluate(() => Blazor.navigateTo("/admin/products"));
await sleep(2000);
await page.screenshot({ path: `${out}/paso-2-productos.png` });

// Step 3: a sale in progress with a weighed item.
await page.evaluate(() => Blazor.navigateTo("/sale"));
await sleep(2000);
const tile = (t) => page.locator(".sale__tiles button", { hasText: t }).first();
await tile("Refresco 2 L").click(); await sleep(500);
await tile("Jugo de naranja").click(); await sleep(500);
await page.getByRole("button", { name: /Carnes/ }).first().click(); await sleep(500);
await tile("Pechuga de pollo").click(); await sleep(900);
await page.keyboard.press("Enter"); await sleep(900);
await page.screenshot({ path: `${out}/paso-3-vender.png` });

await browser.close();
console.log("ok");
