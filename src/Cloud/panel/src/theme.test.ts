import { afterEach, describe, expect, it } from "vitest";
import { getTheme, nextTheme, resolveTheme, setTheme, startTheme } from "./theme";

afterEach(() => localStorage.clear());

describe("theme (TK-183)", () => {
  it("follows the system until the user picks one, and cycles automático → claro → oscuro", () => {
    expect(resolveTheme("system", true)).toBe("dark");
    expect(resolveTheme("system", false)).toBe("light");
    expect(resolveTheme("light", true)).toBe("light");
    expect([nextTheme("system"), nextTheme("light"), nextTheme("dark")]).toEqual(["light", "dark", "system"]);
  });

  it("remembers the choice on this device and marks <html> with the resolved theme", () => {
    startTheme();
    expect(document.documentElement.dataset.theme).toBe("light"); // jsdom has no matchMedia: like a light system
    setTheme("dark");
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(getTheme()).toBe("dark");
    setTheme("system");
    expect(localStorage.getItem("toki.theme")).toBeNull();
    expect(document.documentElement.dataset.theme).toBe("light");
  });
});
