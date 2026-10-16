// TK-228: the Static Web Apps config for the built site. Each public page serves its prerendered HTML, the app's own addresses get the
// empty app (shell.html, so a reload or a link from an e-mail works) and anything else is a real 404 with the prerendered 404.html.
// There is no navigationFallback on purpose: with one, every unknown address would answer 200.

/** SWA route patterns React Router owns outside the public site; `appRedirects` are the older panel addresses that forward to /app. */
export const shellRoutes = (appRedirects) => [
  "/app",
  "/app/*",
  "/c/*",
  "/invite",
  "/invite/*",
  "/unsubscribe",
  ...appRedirects.flatMap((p) => [`/${p}`, `/${p}/*`]),
];

/** `base` is public/staticwebapp.config.json; `pages` the prerendered pages ({ path }); returns the config to write to dist. */
export function swaConfig(base, pages, appRedirects) {
  const file = (path) => (path === "/" ? "/index.html" : `${path}.html`);
  return {
    ...base,
    routes: [
      ...pages.filter((p) => p.path !== "/" && p.path !== "/404").map((p) => ({ route: p.path, rewrite: file(p.path) })),
      ...shellRoutes(appRedirects).map((route) => ({ route, rewrite: "/shell.html" })),
      ...(base.routes ?? []),
    ],
    responseOverrides: { 404: { rewrite: "/404.html", statusCode: 404 } },
  };
}
