import { defineConfig } from "vitest/config";

// shortcut: Vitest 0.34 (with its own Vite 4) because this machine has Node 16 and Vite 6 needs Node 18+;
// move to Vitest 3 (test settings back in vite.config.ts) once Node 22 is installed.
export default defineConfig({
  esbuild: { jsx: "automatic" },
  test: { environment: "jsdom" },
});
