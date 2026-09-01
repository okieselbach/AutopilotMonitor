// Static export: prerender at build time (required with output: "export").
export const dynamic = "force-static";

import type { MetadataRoute } from "next";
import { PAGE_LASTMOD } from "@/utils/page-lastmod.generated";
import { SITE_URL } from "@/utils/config";

const BASE_URL = SITE_URL;

// Build-time only (static export): a URL missing from PAGE_LASTMOD means
// scripts/generate-lastmod.js drifted from this list -- fail the build rather
// than emit "now" as lastmod on every deploy.
function lastmod(urlPath: string): Date {
  const iso = PAGE_LASTMOD[urlPath];
  if (!iso) {
    throw new Error(`sitemap: no lastmod entry for ${urlPath} (add it to scripts/generate-lastmod.js PAGE_MAP)`);
  }
  return new Date(iso);
}

// Documentation and the changelog live at docs.autopilotmonitor.com (GitBook)
// and are indexed there; the old /docs/* and /changelog URLs permanently
// redirect (see staticwebapp.config.json).
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
      url: `${BASE_URL}/get-started`,
      lastModified: lastmod("/get-started"),
      changeFrequency: "monthly",
      priority: 0.8,
    },
    {
      url: `${BASE_URL}/plans`,
      lastModified: lastmod("/plans"),
      changeFrequency: "monthly",
      priority: 0.8,
    },
    {
      url: `${BASE_URL}/buy`,
      lastModified: lastmod("/buy"),
      changeFrequency: "monthly",
      priority: 0.6,
    },
    {
      url: `${BASE_URL}/help`,
      lastModified: lastmod("/help"),
      changeFrequency: "monthly",
      priority: 0.5,
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
