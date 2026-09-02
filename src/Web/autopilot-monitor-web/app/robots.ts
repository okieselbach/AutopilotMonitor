// Static export: prerender at build time (required with output: "export").
export const dynamic = "force-static";

import type { MetadataRoute } from "next";
import { SITE_URL } from "@/utils/config";

// Public marketing / legal pages.
const PUBLIC_PATHS = ["/", "/about", "/buy", "/get-started", "/help", "/plans", "/privacy", "/terms"];

// Authenticated portal routes: nothing to index, and the static shell would
// only ever render a sign-in redirect.
const PRIVATE_PATHS = [
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
];

// Crawlers behind AI answer engines (training, search index, and on-demand
// fetch agents). They already fall under "*"; naming them keeps them allowed
// if the wildcard rule is ever tightened, and states the intent explicitly.
const AI_CRAWLERS = [
  "GPTBot",
  "OAI-SearchBot",
  "ChatGPT-User",
  "ClaudeBot",
  "Claude-SearchBot",
  "Claude-User",
  "PerplexityBot",
  "Perplexity-User",
  "Google-Extended",
];

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      { userAgent: "*", allow: PUBLIC_PATHS, disallow: PRIVATE_PATHS },
      { userAgent: AI_CRAWLERS, allow: PUBLIC_PATHS, disallow: PRIVATE_PATHS },
    ],
    sitemap: `${SITE_URL}/sitemap.xml`,
  };
}
