import type { HealthCheck } from "@/utils/wire-types.generated";

/**
 * Presentation-side mirror of the server's operator-only rule for the detailed health report
 * (HealthCheckFunction.GetDetailedHealthCheck): the server strips these for non-Global-Admins,
 * and this makes the page match that shape when the Global-Admin VIEW is off (demo mode), so a
 * live demo looks exactly like a tenant admin's page. Pure logic, no DOM — see lib/demoMode.ts.
 *
 * NOT a security boundary: the real gate is the server, which never sends this data to non-GAs.
 */

/** Checks the server only returns to Global Admins: internal infrastructure topology. */
export const OPERATOR_ONLY_CHECKS: ReadonlySet<string> = new Set(["SignalR Quota", "Poison Queues"]);

/** True for detail values that are URLs — the server includes these only for Global Admins. */
export function isUrlDetail(value: unknown): value is string {
  return typeof value === "string" && /^https?:\/\//i.test(value);
}

/**
 * Drops the operator-only cards and the endpoint-URL detail rows unless the operator view is on.
 * Returns the input untouched (same reference) when nothing needs hiding.
 */
export function visibleHealthChecks(checks: readonly HealthCheck[], operatorView: boolean): HealthCheck[] {
  if (operatorView) return checks as HealthCheck[];
  return checks
    .filter((c) => !OPERATOR_ONLY_CHECKS.has(c.name))
    .map((c) => ({ ...c, details: visibleHealthDetails(c.details, false) }));
}

/** Removes URL-valued rows unless the operator view is on; undefined when nothing is left. */
export function visibleHealthDetails(
  details: Record<string, unknown> | undefined,
  operatorView: boolean,
): Record<string, unknown> | undefined {
  if (!details || operatorView) return details;
  const kept = Object.entries(details).filter(([, v]) => !isUrlDetail(v));
  return kept.length > 0 ? Object.fromEntries(kept) : undefined;
}
