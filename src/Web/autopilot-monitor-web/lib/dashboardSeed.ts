import { api } from "@/lib/api";
import { CORRELATION_HEADER, newCorrelationId } from "@/lib/correlationId";
import { registerSeed } from "@/lib/prefetchSeeds";
import {
  DASHBOARD_STATS_DEFAULT_DAYS,
  getInitialSessionsPageSize,
} from "@/app/dashboard/hooks/sessionsPageSize";

const DASHBOARD_PATHS = new Set(["/dashboard/", "/dashboard"]);

/**
 * Starts the dashboard's first two data requests (own-tenant session list and stats) in
 * parallel with `/api/auth/me`, as response seeds for `authenticatedFetch`
 * (lib/prefetchSeeds.ts). The URLs must be exactly what useDashboardSessions and
 * useDashboardStats build for their initial fetch; a mismatch only wastes one request.
 *
 * Own-tenant scope only: the backend takes the tenant from the JWT; `tenantId` (the Entra
 * tenant of the signed-in account) only gates the seed on a known account. Cross-tenant
 * (Global-Admin aggregate) mode uses other endpoints, so nothing is seeded while that mode
 * is switched on.
 */
export function seedDashboardFirstFetch(accessToken: string, tenantId: string | undefined): void {
  if (typeof window === "undefined" || !tenantId) return;
  if (!DASHBOARD_PATHS.has(window.location.pathname)) return;
  try {
    if (window.localStorage.getItem("globalAdminMode") === "true") return;
  } catch {
    return;
  }

  const urls = [
    // No tenantId query: the endpoint takes the tenant from the JWT, and useDashboardSessions
    // builds the same URL without it (the seed must match it byte for byte).
    api.sessions.list(undefined, undefined, { pageSize: getInitialSessionsPageSize() }),
    api.sessions.stats({ days: DASHBOARD_STATS_DEFAULT_DAYS }),
  ];
  for (const url of urls) {
    const correlationId = newCorrelationId();
    registerSeed(
      url,
      correlationId,
      fetch(url, {
        headers: { Authorization: `Bearer ${accessToken}`, [CORRELATION_HEADER]: correlationId },
        signal: AbortSignal.timeout(30_000),
      }),
    );
  }
}
