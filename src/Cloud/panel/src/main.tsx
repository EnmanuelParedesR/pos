import { StrictMode } from "react";
import { createRoot, hydrateRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
// One set of tokens for the register and the panel (Escuadra), plus the back office's layer (Escuadra Admin).
import "../../../Client/Abastalia.Escuadra/wwwroot/escuadra.css";
import "../../../Client/Abastalia.Escuadra/wwwroot/escuadra-admin.css";
import "./panel.css";
import "./pages/site/site.css";
import { App } from "./App";
import { startTheme } from "./theme";

startTheme();
// TK-278: a tab opened before a deploy asks for a lazy chunk that no longer exists; reloading gets the new build.
window.addEventListener("vite:preloadError", () => location.reload());

const root = document.getElementById("root")!;
const app = (
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>
);
// TK-261: a prerendered public page is hydrated, so its text stays on screen while the app loads; shell.html starts empty.
if (root.hasChildNodes()) hydrateRoot(root, app);
else createRoot(root).render(app);
