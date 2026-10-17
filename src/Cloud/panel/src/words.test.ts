import { expect, it } from "vitest";

// TK-353: the panel never says "fiado" either (TK-312 is the site's own check): "A crédito", "Le deben (cuentas por cobrar)", "Abonos de crédito".
const sources = import.meta.glob(["./**/*.{ts,tsx}", "!./**/*.test.{ts,tsx}"], { query: "?raw", import: "default", eager: true }) as Record<string, string>;

it("TK-353: no panel source says fiado outside a comment", () => {
  const found = Object.entries(sources).flatMap(([file, text]) =>
    text.split("\n").flatMap((line, i) => (!/^\s*(\/\/|\*|\/\*)/.test(line) && /\bfiad[oa]s?\b|\bfiar\b|\bfía\b/i.test(line) ? [`${file}:${i + 1}`] : [])));
  expect(Object.keys(sources).length).toBeGreaterThan(20);
  expect(found).toEqual([]);
});
