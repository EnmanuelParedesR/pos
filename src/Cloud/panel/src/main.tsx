import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router";
// One set of tokens for the register and the panel (Escuadra), plus the back office's layer (Escuadra Admin).
import "../../../Client/Toki.Escuadra/wwwroot/escuadra.css";
import "../../../Client/Toki.Escuadra/wwwroot/escuadra-admin.css";
import "./panel.css";
import "./pages/site/site.css";
import { App } from "./App";
import { startTheme } from "./theme";

startTheme();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <App />
    </BrowserRouter>
  </StrictMode>,
);
