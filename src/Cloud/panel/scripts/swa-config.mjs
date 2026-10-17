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

/** R11 B-Med5: what keeps another site's script out of the panel (the refresh token is in localStorage) and the panel out of another site's frame.
 * Scripts only from this site; the API is the only other address it talks to. Styles allow inline: React's `style` props and the prerendered
 * pages carry them, and a style cannot read the token. */
const paddle = { cdn: "https://cdn.paddle.com", any: "https://*.paddle.com", checkout: "https://buy.paddle.com https://sandbox-buy.paddle.com" };

export function securityHeaders(apiUrl) {
  return {
    "Content-Security-Policy": [
      "default-src 'self'",
      // TK-249: the billing page loads Paddle.js from Paddle's CDN and opens its checkout in a frame (Paddle's own CSP list).
      `script-src 'self' ${paddle.cdn}`,
      `style-src 'self' 'unsafe-inline' ${paddle.cdn}`,
      // R12 #12: the public catalog shows the store's logo, served by the API.
      `img-src 'self' data: ${new URL(apiUrl).origin} ${paddle.any}`,
      "font-src 'self' data:",
      `connect-src 'self' ${new URL(apiUrl).origin} ${paddle.any}`,
      `frame-src ${paddle.checkout}`,
      "frame-ancestors 'none'",
      "base-uri 'self'",
      "form-action 'self'",
      "object-src 'none'",
    ].join("; "),
    "Strict-Transport-Security": "max-age=31536000; includeSubDomains",
    "X-Frame-Options": "DENY",
  };
}

/** `base` is public/staticwebapp.config.json; `pages` the prerendered pages ({ path }); `apiUrl` the cloud API the build talks to
 * (VITE_API_URL); returns the config to write to dist. */
export function swaConfig(base, pages, appRedirects, apiUrl = "http://localhost:5100") {
  const file = (path) => (path === "/" ? "/index.html" : `${path}.html`);
  return {
    ...base,
    globalHeaders: { ...base.globalHeaders, ...securityHeaders(apiUrl) },
    routes: [
      ...pages.filter((p) => p.path !== "/" && p.path !== "/404").map((p) => ({ route: p.path, rewrite: file(p.path) })),
      ...shellRoutes(appRedirects).map((route) => ({ route, rewrite: "/shell.html" })),
      ...(base.routes ?? []),
    ],
    responseOverrides: { 404: { rewrite: "/404.html", statusCode: 404 } },
  };
}
