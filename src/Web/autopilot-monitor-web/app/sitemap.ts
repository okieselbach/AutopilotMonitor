import type { MetadataRoute } from "next";
import { PAGE_LASTMOD } from "@/utils/page-lastmod.generated";

const BASE_URL = "https://www.autopilotmonitor.com";

function lastmod(urlPath: string): Date {
  const iso = PAGE_LASTMOD[urlPath];
  return iso ? new Date(iso) : new Date();
}

// Documentation lives at docs.autopilotmonitor.com (GitBook) and is indexed
// there; the old /docs/* URLs permanently redirect (see next.config.ts).
export default function sitemap(): MetadataRoute.Sitemap {
  return [
    {
      url: `${BASE_URL}/`,
      lastModified: lastmod("/"),
      changeFrequency: "monthly",
      priority: 1,
    },
    {
      url: `${BASE_URL}/about`,
      lastModified: lastmod("/about"),
      changeFrequency: "monthly",
      priority: 0.7,
    },
    {
      url: `${BASE_URL}/roadmap`,
      lastModified: lastmod("/roadmap"),
      changeFrequency: "weekly",
      priority: 0.7,
    },
    {
      url: `${BASE_URL}/privacy`,
      lastModified: lastmod("/privacy"),
      changeFrequency: "yearly",
      priority: 0.3,
    },
    {
      url: `${BASE_URL}/terms`,
      lastModified: lastmod("/terms"),
      changeFrequency: "yearly",
      priority: 0.3,
    },
  ];
}
