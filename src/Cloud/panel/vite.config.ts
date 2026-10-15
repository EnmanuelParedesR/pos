/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  // escuadra.css and escuadra-admin.css come from the register's design system so both share one set of tokens.
  server: { fs: { allow: [".", "../../Client/Toki.Escuadra/wwwroot"] } },
  test: { environment: "jsdom" },
});
