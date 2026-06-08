import type { NextConfig } from "next";
import withBundleAnalyzer from "@next/bundle-analyzer";

const nextConfig: NextConfig = {
  reactStrictMode: true,
  async redirects() {
    return [
      // Permanent redirect for old /landing URL — all SEO equity flows to /
      { source: "/landing", destination: "/", permanent: true },
      // Docs index redirects to default section
      { source: "/docs", destination: "/docs/overview", permanent: false },
    ];
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
              "script-src 'self' 'unsafe-inline'",
              "style-src 'self' 'unsafe-inline'",
              "img-src 'self' data: blob: https://*.tile.openstreetmap.org",
              "font-src 'self'",
              "connect-src 'self' https://autopilotmonitor-api.azurewebsites.net https://autopilotmonitor.blob.core.windows.net https://login.microsoftonline.com https://*.service.signalr.net wss://*.service.signalr.net https://js.monitor.azure.com https://*.in.applicationinsights.azure.com",
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
