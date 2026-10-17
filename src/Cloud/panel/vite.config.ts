/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The project has no @types/node; the config only reads and sets environment variables.
declare const process: { env: Record<string, string | undefined> };

export default defineConfig(async () => {
  // TK-285: SITE_GATE_CODE (given by the owner, never in the repo) puts the public site behind that code; only its SHA-256 reaches the
  // bundle. The deployer can pass VITE_SITE_GATE (the hash) instead. Neither set = no gate. Tests ignore it: they stub the hash themselves.
  const gateCode = process.env.SITE_GATE_CODE?.trim();
  if (gateCode && !process.env.VITEST) {
    const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(gateCode));
    process.env.VITE_SITE_GATE = [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
  }
  return {
    plugins: [react()],
    // escuadra.css and escuadra-admin.css come from the register's design system so both share one set of tokens.
    server: { fs: { allow: [".", "../../Client/Abastalia.Escuadra/wwwroot"] } },
    // A test outlasts its waits: findBy may take up to 5 s on a loaded PC or CI runner (src/test-setup.ts), so a test gets 15 s, not 5.
    test: { environment: "jsdom", setupFiles: ["src/test-setup.ts"], testTimeout: 15_000 },
  };
});
