// TK-281: responsive images for the public site. Run after changing an original:
//   node scripts/images.mjs
// Originals live in design/media (photos from Pexels, screenshots of the real register); output goes to public/img/site
// as AVIF and WebP at three widths, used with <picture>/srcset (see Photo in SiteParts.tsx).
import sharp from "sharp";
import { mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "../../../..");
const out = join(root, "src/Cloud/panel/public/img/site");
export const widths = [480, 800, 1200];

/** name → [original, crop ratio (w/h), sharp position or the region to cut first]. Photos are cropped to the frame they use on the page;
 *  screenshots are cut to the part each step talks about (the form, the product rows, the cart and total) so they read at 360 px. */
const images = {
  carniceria: ["design/media/fotos/carniceria.jpg", 4 / 3, "attention"],
  colmado: ["design/media/fotos/colmado.jpg", 4 / 3, "centre"],
  deli: ["design/media/fotos/deli.jpg", 4 / 3, "centre"],
  "paso-1-instalar": ["design/media/capturas/paso-1-instalar.png", 16 / 10, { left: 341, top: 287, width: 929, height: 581 }],
  "paso-2-productos": ["design/media/capturas/paso-2-productos.png", 16 / 10, { left: 420, top: 256, width: 1178, height: 736 }],
  "paso-3-vender": ["design/media/capturas/paso-3-vender.png", 16 / 10, { left: 574, top: 164, width: 1465, height: 916 }],
};

mkdirSync(out, { recursive: true });
for (const [name, [src, ratio, position]] of Object.entries(images)) {
  for (const w of widths) {
    const resize = { width: w, height: Math.round(w / ratio), fit: "cover", position: typeof position === "string" ? position : "centre" };
    const image = sharp(join(root, src));
    const base = (typeof position === "object" ? image.extract(position) : image).resize(resize);
    await base.clone().avif({ quality: 52, effort: 6 }).toFile(join(out, `${name}-${w}.avif`));
    await base.clone().webp({ quality: 72 }).toFile(join(out, `${name}-${w}.webp`));
  }
  console.log(name);
}
