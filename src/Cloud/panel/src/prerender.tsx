// Build-time entry (TK-228): scripts/prerender.mjs renders each public page to HTML so search engines read it without running JavaScript.
import { renderToString } from "react-dom/server";
import { StaticRouter } from "react-router";
import { App } from "./App";
import { site } from "./pages/site/config";
import { notFound, sitePages } from "./pages/site/SitePages";

export { site };
export const pages = [...sitePages, { ...notFound, path: "/404" }].map(({ path, title, description }) => ({ path, title, description }));

export const render = (path: string) =>
  renderToString(
    <StaticRouter location={path}>
      <App />
    </StaticRouter>,
  );
