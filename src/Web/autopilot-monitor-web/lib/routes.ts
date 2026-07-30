/**
 * Canonical in-app URL builders — the single definition of every deep-linkable
 * route shape. All link sites MUST build URLs through these helpers; never
 * hand-concatenate `/sessions/...` etc. in components.
 *
 * The detail routes are query-string based (`/sessions?id=...`) because the
 * portal is a static export: path parameters would require prerendering
 * unbounded id spaces. Legacy path-shape URLs (`/sessions/{id}` from old
 * Teams/Slack/webhook notifications) are rewritten client-side by
 * LegacyPathRedirect using the same builders.
 *
 * Rule: the fragment (#...) always comes AFTER the query string.
 */

import type { Route } from "next";

function withQuery<T extends string>(
  path: Route<T>,
  params: Record<string, string | undefined>,
  hash?: string,
): Route {
  const qs = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== "")
    .map(([k, v]) => `${k}=${encodeURIComponent(v as string)}`)
    .join("&");
  const fragment = hash ? (hash.startsWith("#") ? hash : `#${hash}`) : "";
  // Appending a query/fragment to an already-validated Route keeps it valid.
  return `${path}${qs ? `?${qs}` : ""}${fragment}` as Route;
}

export function sessionUrl(
  sessionId: string,
  opts?: { tenantId?: string; hash?: string },
): Route {
  return withQuery("/sessions", { id: sessionId, tenantId: opts?.tenantId }, opts?.hash);
}

export function inspectorUrl(sessionId: string, opts?: { tab?: string }): Route {
  return withQuery("/sessions/inspector", { id: sessionId, tab: opts?.tab });
}

export function diagnosisUrl(sessionId: string): Route {
  return withQuery("/diagnosis", { id: sessionId });
}

export function appDetailUrl(
  appName: string,
  opts?: { days?: string; tenantId?: string },
): Route {
  return withQuery("/apps/detail", {
    name: appName,
    days: opts?.days,
    tenantId: opts?.tenantId,
  });
}

export function backupUrl(backupId: string): Route {
  return withQuery("/admin/backups/detail", { id: backupId });
}

export function deviceBlockUrl(
  sessionId: string,
  reason: string,
  action?: "Block" | "Kill",
): Route {
  return withQuery("/admin/security/device-block", { sessionId, reason, action });
}

export function customsArchiveUrl(tenantId: string, historyRowKey: string): Route {
  return withQuery("/admin/customs-archive/detail", {
    tenantId,
    rowKey: historyRowKey,
  });
}

/**
 * Compile-time validates a route literal that targets a DYNAMIC route (e.g.
 * `/admin/settings/[section]`): the bare `Route` type only covers static
 * routes, so dynamic-section literals — nav configs, section index redirects —
 * go through this identity helper, where inference of `T` runs the literal
 * against the generated route union. A typo'd prefix is a compile error.
 */
export function route<T extends string>(href: Route<T>): Route {
  return href as Route;
}

/**
 * Brands an in-app href that only exists at runtime (backend-emitted
 * notification deep links, the persisted post-login return URL) for the typed
 * router APIs. The compiler cannot validate data, only literals — producers of
 * these hrefs are responsible for emitting canonical shapes (ideally via the
 * builders above). Keep every such cast behind this single seam.
 */
export function trustedRoute(href: string): Route {
  return href as Route;
}
