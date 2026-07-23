import type { MetadataRoute } from "next";
import { SITE_URL } from "@/utils/config";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: ["/", "/about", "/privacy", "/terms"],
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
          "/sessions/",
          "/diagnosis/",
          "/admin/",
          "/settings",
          "/preview",
        ],
      },
    ],
    sitemap: `${SITE_URL}/sitemap.xml`,
  };
}
