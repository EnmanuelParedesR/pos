import { renderToStaticMarkup } from "react-dom/server";
import { expect, test } from "vitest";
import avatar from "../public/og/talia.svg?raw";
import { Talia } from "./ui";

const markup = () =>
  renderToStaticMarkup(<Talia size={120} />).replace("<svg ", '<svg xmlns="http://www.w3.org/2000/svg" ').replace(' class="esc-talia" aria-hidden="true"', "");

// TK-349: the PNG avatars (scripts/talia-avatar.mjs) come from public/og/talia.svg, which must be the component's drawing.
// After changing <Talia />: paste the markup this test prints into public/og/talia.svg, then run node scripts/talia-avatar.mjs.
test("Talia's avatar file is the component's SVG", () => {
  expect(avatar.trim(), `public/og/talia.svg should be:\n${markup()}`).toBe(markup());
});

// TK-349: at chat and dialog-header sizes the hang hole goes (it read as a third eye); from 48 px it is there.
test("small Talias have no hang hole and big ones do", () => {
  const hole = (size: number) => renderToStaticMarkup(<Talia size={size} />).includes("A8 8");
  expect(hole(28)).toBe(false);
  expect(hole(32)).toBe(false);
  expect(hole(36)).toBe(false);
  expect(hole(48)).toBe(true);
  expect(renderToStaticMarkup(<Talia size={28} mood="happy" />)).not.toBe(renderToStaticMarkup(<Talia size={28} />));
});
