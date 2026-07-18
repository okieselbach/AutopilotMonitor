import type { NextConfig } from "next";
import withBundleAnalyzer from "@next/bundle-analyzer";

const nextConfig: NextConfig = {
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
  async redirects() {
    // Documentation moved to GitBook (docs.autopilotmonitor.com). Specific
    // per-section mappings first (Next.js uses the first matching redirect),
    // then a catch-all so no old deep link ever 404s.
    const DOCS = "https://docs.autopilotmonitor.com";
    const docsMoves: Array<[string, string]> = [
      ["/docs/private-preview", "/getting-started/requirements-and-access"],
      ["/docs/overview", "/"],
      ["/docs/general", "/concepts/roles-and-permissions"],
      ["/docs/setup", "/getting-started/portal-setup"],
      ["/docs/agent", "/concepts/agent-lifecycle-and-security"],
      ["/docs/agent-setup", "/getting-started/deploy-the-agent"],
      ["/docs/settings", "/reference/settings"],
      ["/docs/gather-rules", "/rules/gather-rules"],
      ["/docs/analyze-rules", "/rules/analyze-rules"],
      ["/docs/ime-log-patterns", "/rules/ime-log-patterns"],
      ["/docs/faq", "/troubleshooting/faq"],
      ["/docs/known-issues", "/troubleshooting/service-announcements"],
      ["/docs/mcp-integration", "/integrations/ai-integration-mcp"],
      ["/docs/agent-changelog", "/changelog/agent-changelog"],
    ];
    return [
      // Permanent redirect for old /landing URL — all SEO equity flows to /
      { source: "/landing", destination: "/", permanent: true },
      ...docsMoves.map(([source, dest]) => ({
        source,
        destination: `${DOCS}${dest === "/" ? "" : dest}`,
        permanent: true,
      })),
      // Everything else under /docs (incl. retired sections like agent-internals)
      { source: "/docs", destination: DOCS, permanent: true },
      { source: "/docs/:path*", destination: DOCS, permanent: true },
      // Platform changelog moved into the GitBook docs as well
      { source: "/changelog", destination: `${DOCS}/changelog/platform-changelog`, permanent: true },
      // Roadmap page retired for now (may return under docs later) — non-permanent on purpose
      { source: "/roadmap", destination: "/", permanent: false },
    ];
  },
  async rewrites() {
    // Dev-only reverse proxy: with DEV_API_PROXY_TARGET set in .env.local,
    // /api/* is forwarded server-side through the Next dev server to the
    // deployed backend. The browser stays same-origin, so the prod Function
    // App needs no localhost CORS entry. Inactive in production builds.
    const target = process.env.DEV_API_PROXY_TARGET;
    if (process.env.NODE_ENV !== "development" || !target) {
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
              // Dev-only: next dev serves HMR/react-refresh chunks through eval();
              // production stays strict without 'unsafe-eval'.
              `script-src 'self' 'unsafe-inline'${process.env.NODE_ENV === "development" ? " 'unsafe-eval'" : ""}`,
              "style-src 'self' 'unsafe-inline'",
              "img-src 'self' data: blob: https://*.tile.openstreetmap.org",
              "font-src 'self'",
              "connect-src 'self' https://autopilotmonitor-api-eu.azurewebsites.net https://autopilotmonitoreu.blob.core.windows.net https://login.microsoftonline.com https://*.service.signalr.net wss://*.service.signalr.net https://js.monitor.azure.com https://*.in.applicationinsights.azure.com",
              "frame-ancestors 'none'",
            ].join("; "),
          },
        ],
      },
    ];
  },
};

// Run `ANALYZE=1 npm run build` to emit interactive treemaps of the
// client/server bundles into .next/analyze/ for bundle-size investigation.
export default withBundleAnalyzer({ enabled: process.env.ANALYZE === "1" })(
  nextConfig,
);
