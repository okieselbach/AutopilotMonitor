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

function withQuery(
  path: string,
  params: Record<string, string | undefined>,
  hash?: string,
): string {
  const qs = Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== "")
    .map(([k, v]) => `${k}=${encodeURIComponent(v as string)}`)
    .join("&");
  const fragment = hash ? (hash.startsWith("#") ? hash : `#${hash}`) : "";
  return `${path}${qs ? `?${qs}` : ""}${fragment}`;
}

export function sessionUrl(
  sessionId: string,
  opts?: { tenantId?: string; hash?: string },
): string {
  return withQuery("/sessions", { id: sessionId, tenantId: opts?.tenantId }, opts?.hash);
}

export function inspectorUrl(sessionId: string, opts?: { tab?: string }): string {
  return withQuery("/sessions/inspector", { id: sessionId, tab: opts?.tab });
}

export function diagnosisUrl(sessionId: string): string {
  return withQuery("/diagnosis", { id: sessionId });
}

export function appDetailUrl(
  appName: string,
  opts?: { days?: string; tenantId?: string },
): string {
  return withQuery("/apps/detail", {
    name: appName,
    days: opts?.days,
    tenantId: opts?.tenantId,
  });
}

export function backupUrl(backupId: string): string {
  return withQuery("/admin/backups/detail", { id: backupId });
}

export function customsArchiveUrl(tenantId: string, historyRowKey: string): string {
  return withQuery("/admin/customs-archive/detail", {
    tenantId,
    rowKey: historyRowKey,
  });
}
