import { expect, it, vi } from "vitest";
import { reloadOnce } from "./reload";

it("R12 #12: a missing chunk reloads the page once, not in a loop", () => {
  const reload = vi.fn();
  reloadOnce(sessionStorage, 1_000_000, reload);
  reloadOnce(sessionStorage, 1_002_000, reload); // the reloaded page fails again at once
  expect(reload).toHaveBeenCalledTimes(1);
  reloadOnce(sessionStorage, 1_020_000, reload); // a later deploy, much later
  expect(reload).toHaveBeenCalledTimes(2);
});
