// TK-349: Talia's PNG avatars (WhatsApp profile, social cards) from the same SVG the panel draws (ui.tsx, <Talia />). Run after changing her:
//   node scripts/talia-avatar.mjs
// public/og/talia.svg is the component drawn at 120 px; src/talia.test.tsx checks it and prints the markup to paste.
import sharp from "sharp";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const og = join(dirname(fileURLToPath(import.meta.url)), "../public/og");
const svg = readFileSync(join(og, "talia.svg"));
// 1024: WhatsApp and the social cards crop it to a circle themselves; the berenjena circle already is one, the corners stay transparent.
await sharp(svg, { density: 1024 / 120 * 72 }).resize(1024, 1024).png().toFile(join(og, "talia.png"));
console.log("public/og/talia.png");
