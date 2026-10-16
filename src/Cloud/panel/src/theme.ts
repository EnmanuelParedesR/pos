/** Dark mode (TK-183), same rules as Escuadra's escuadra.js: the choice is kept per device, and <html data-theme> is always the resolved
 *  theme ("light" or "dark") that escuadra-admin.css reads. "system" follows prefers-color-scheme, also when it changes. */
export type ThemeChoice = "system" | "light" | "dark";

const key = "abastalia.theme";
let unsaved: ThemeChoice = "system"; // storage blocked: the choice lasts until the page reloads

const darkQuery = () => (typeof window.matchMedia === "function" ? window.matchMedia("(prefers-color-scheme: dark)") : null);

export function getTheme(): ThemeChoice {
  try {
    const value = localStorage.getItem(key);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return unsaved;
  }
}

export const resolveTheme = (choice: ThemeChoice, prefersDark: boolean): "light" | "dark" => (choice === "system" ? (prefersDark ? "dark" : "light") : choice);

/** Sistema → Claro → Oscuro → Sistema. */
export const nextTheme = (choice: ThemeChoice): ThemeChoice => (choice === "system" ? "light" : choice === "light" ? "dark" : "system");

export const themeName = (choice: ThemeChoice) => (choice === "light" ? "claro" : choice === "dark" ? "oscuro" : "automático");

function apply() {
  document.documentElement.dataset.theme = resolveTheme(getTheme(), darkQuery()?.matches ?? false);
}

export function setTheme(choice: ThemeChoice) {
  unsaved = choice;
  try {
    if (choice === "system") localStorage.removeItem(key);
    else localStorage.setItem(key, choice);
  } catch {
    // kept in `unsaved`
  }
  apply();
}

/** Before the first render, so the page never flashes light. */
export function startTheme() {
  apply();
  darkQuery()?.addEventListener("change", apply);
}
