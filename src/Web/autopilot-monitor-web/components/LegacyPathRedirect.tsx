"use client";

import { useEffect } from "react";
import { usePathname, useRouter } from "next/navigation";
import type { Route } from "next";
import {
  appDetailUrl,
  backupUrl,
  customsArchiveUrl,
  diagnosisUrl,
  inspectorUrl,
  sessionUrl,
} from "@/lib/routes";

/**
 * Permanent client-side rewrite of legacy PATH-shaped detail URLs to the
 * canonical query-string form. Old Teams/Slack/webhook notifications carry
 * /sessions/{id} links forever (sent messages cannot be edited), and bookmarks
 * exist for every family — this component keeps them all working.
 *
 * It is mounted GLOBALLY in the root layout on purpose: the SWA rewrite rules
 * are wildcard-per-family (e.g. /sessions/* serves the sessions page HTML), so
 * /sessions/{id}/inspector is served the WRONG page's HTML — only a global
 * matcher sees every legacy pathname regardless of which page hydrated.
 *
 * Existing query params (e.g. ?tenantId=) and the fragment (#event-…) are
 * preserved; the canonical builders in lib/routes.ts are the single source of
 * the target shapes.
 */

// Real static pages under /sessions/ — never legacy session ids. Every new
// /sessions/<subpage> route MUST be added here, or the legacy rewrite below
// hijacks it into /sessions?id=<subpage>.
const SESSIONS_STATIC_SIBLINGS = new Set(["inspector", "network-timeline"]);

function legacyTarget(pathname: string, search: URLSearchParams, hash: string): Route | null {
  const seg = pathname.replace(/\/+$/, "").split("/").filter(Boolean).map(decodeURIComponent);

  // /sessions/{id}/inspector
  if (seg.length === 3 && seg[0] === "sessions" && seg[2] === "inspector") {
    return inspectorUrl(seg[1], { tab: search.get("tab") ?? undefined });
  }
  // /sessions/{id} — a static sibling as an id cannot occur (the static page wins)
  if (seg.length === 2 && seg[0] === "sessions" && !SESSIONS_STATIC_SIBLINGS.has(seg[1])) {
    return sessionUrl(seg[1], {
      tenantId: search.get("tenantId") ?? undefined,
      hash: hash || undefined,
    });
  }
  // /diagnosis/{id}
  if (seg.length === 2 && seg[0] === "diagnosis") {
    return diagnosisUrl(seg[1]);
  }
  // /apps/{name}
  if (seg.length === 2 && seg[0] === "apps" && seg[1] !== "detail") {
    return appDetailUrl(seg[1], {
      days: search.get("days") ?? undefined,
      tenantId: search.get("tenantId") ?? undefined,
    });
  }
  // /admin/backups/{id}
  if (seg.length === 3 && seg[0] === "admin" && seg[1] === "backups" && seg[2] !== "detail") {
    return backupUrl(seg[2]);
  }
  // /admin/customs-archive/{tenantId}/{historyRowKey}
  if (seg.length === 4 && seg[0] === "admin" && seg[1] === "customs-archive" && seg[2] !== "detail") {
    return customsArchiveUrl(seg[2], seg[3]);
  }

  return null;
}

export function LegacyPathRedirect() {
  const pathname = usePathname();
  const router = useRouter();

  useEffect(() => {
    if (!pathname) return;
    const target = legacyTarget(
      pathname,
      new URLSearchParams(window.location.search),
      window.location.hash,
    );
    if (target) router.replace(target);
  }, [pathname, router]);

  return null;
}

// Exported for the unit test that keeps the mapping table honest.
export { legacyTarget };
