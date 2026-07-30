import type { NextConfig } from "next";
import withBundleAnalyzer from "@next/bundle-analyzer";
import { API_URL_PROD, BLOB_URL_PROD, ENTRA_LOGIN_URL } from "./utils/config";

const isDev = process.env.NODE_ENV === "development";

const nextConfig: NextConfig = {
  // Full static export — the SWA serves plain files from the edge; there is no
  // managed SSR runtime (and therefore no cold start) anymore. Redirects and
  // response headers moved to staticwebapp.config.json (guarded by
  // utils/__tests__/swaConfig.guard.test.ts); the legacy path-shaped detail
  // URLs are handled by SWA rewrites + components/LegacyPathRedirect.tsx.
  output: "export",
  // folder/index.html output — unambiguous rewrite targets for the SWA config.
  trailingSlash: true,
  // DEV-ONLY. Without this, `next dev` 308-redirects /api/foo -> /api/foo/
  // BEFORE the dev proxy rewrite runs, and the Functions backend rejects
  // trailing-slash routes — which silently breaks local sign-in (/me fails).
  // It must NOT be set for the production build: the flag also defines
  // __NEXT_MANUAL_TRAILING_SLASH, which turns the client router's
  // trailing-slash normalization into a no-op. Under output:"export" the
  // router then fetches RSC payloads as /<path>.txt instead of the
  // /<path>/index.txt the export actually emits — every fetch 404s and EVERY
  // <Link> navigation silently degrades to a full-page (MPA) load, resetting
  // all client state (e.g. the sidebar collapses on each click).
  ...(isDev ? { skipTrailingSlashRedirect: true } : {}),
  reactStrictMode: true,
  experimental: {
    // Rewrite these heavy packages to per-module imports so unused exports are
    // tree-shaken out of the route chunks that touch them.
    optimizePackageImports: [
      "recharts",
      "@xyflow/react",
      "@microsoft/signalr",
      "@azure/msal-react",
    ],
  },
  // rewrites()/headers() are unsupported under output:'export' — the keys are
  // dev-gated so `next build` stays warning-free while `next dev` keeps the
  // API proxy and CSP parity with production.
  ...(isDev
    ? {
        async rewrites() {
          // Dev-only reverse proxy: with DEV_API_PROXY_TARGET set in .env.local,
          // /api/* is forwarded server-side through the Next dev server to the
          // deployed backend. The browser stays same-origin, so the prod Function
          // App needs no localhost CORS entry.
          const target = process.env.DEV_API_PROXY_TARGET;
          if (!target) {
            return [];
          }
          return [{ source: "/api/:path*", destination: `${target}/api/:path*` }];
        },
        async headers() {
          return [
            {
              source: "/:path*",
              headers: [
                { key: "X-Content-Type-Options", value: "nosniff" },
                { key: "Permissions-Policy", value: "unload=()" },
                { key: "X-Frame-Options", value: "DENY" },
                { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
                {
                  key: "Content-Security-Policy",
                  value: [
                    "default-src 'self'",
                    // Dev-only: next dev serves HMR/react-refresh chunks through
                    // eval(); production CSP (staticwebapp.config.json) stays
                    // strict without 'unsafe-eval'.
                    "script-src 'self' 'unsafe-inline' 'unsafe-eval'",
                    "style-src 'self' 'unsafe-inline'",
                    "img-src 'self' data: blob: https://*.tile.openstreetmap.org",
                    "font-src 'self'",
                    `connect-src 'self' ${API_URL_PROD} ${BLOB_URL_PROD} ${ENTRA_LOGIN_URL} https://*.service.signalr.net wss://*.service.signalr.net https://js.monitor.azure.com https://*.in.applicationinsights.azure.com`,
                    "frame-ancestors 'none'",
                  ].join("; "),
                },
              ],
            },
          ];
        },
      }
    : {}),
};

// Run `ANALYZE=1 npm run build` to emit interactive treemaps of the
// client/server bundles into .next/analyze/ for bundle-size investigation.
export default withBundleAnalyzer({ enabled: process.env.ANALYZE === "1" })(
  nextConfig,
);
