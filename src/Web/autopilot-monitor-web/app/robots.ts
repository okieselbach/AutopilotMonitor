// Static export: prerender at build time (required with output: "export").
export const dynamic = "force-static";

import type { MetadataRoute } from "next";
import { SITE_URL } from "@/utils/config";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: ["/", "/about", "/get-started", "/privacy", "/terms"],
        disallow: [
          "/dashboard",
          "/fleet-health",
          "/health-check",
          "/usage-metrics",
          "/audit",
          "/progress",
          "/gather-rules",
          "/analyze-rules",
          "/ime-log-patterns",
          "/geographic-performance",
          // No trailing slash: robots prefixes then cover /sessions, /sessions/…
          // AND the query-string form /sessions?id=… (detail routes are
          // query-based since the static export).
          "/sessions",
          "/diagnosis",
          "/sla",
          "/admin/",
          "/settings",
          "/activation",
        ],
      },
    ],
    sitemap: `${SITE_URL}/sitemap.xml`,
  };
}
