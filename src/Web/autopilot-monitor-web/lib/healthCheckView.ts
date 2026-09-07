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

// ── MCP card: cold start, and how the card recovers from it ────────────────────

/**
 * The MCP Container App runs minReplicas=0. A cold start is 20-30s of Container Apps
 * activation, so the backend probe gives up after ~3s and answers status "warming"
 * (see HealthCheckService.CheckMcpServerAsync) rather than holding the request. The card
 * then re-polls on its own until the replica is up.
 *
 * 5s x 12 attempts ≈ 60s of coverage. The measured activation is 13-25s (worst observed
 * first byte 31.5s), but a 40s budget was seen to expire on a real cold start — Container
 * Apps activation has no upper bound we control — so the budget is 60s. Shorter intervals
 * buy nothing (every attempt costs the backend a 3s held request), and stretching it much
 * further would turn a genuinely broken container into a silent forever-spinner instead of
 * an honest warning.
 */
export const MCP_WARMING_POLL_MS = 5_000;
export const MCP_WARMING_MAX_ATTEMPTS = 12;

/**
 * Client-side budget for one probe. Comfortably above the backend's ~3s answer, and
 * deliberately NOT tighter: authenticatedFetch reuses the caller's signal for its 401
 * retry, so a nearly-spent budget would abort the retry on arrival.
 */
export const MCP_PROBE_TIMEOUT_MS = 10_000;

const MCP_CARD_BASE = {
  name: "MCP Server",
  description: "AI query interface availability",
} as const;

export interface McpCardState {
  /** What the card renders. */
  display: HealthCheck;
  /** What the overall banner and the healthy/total counts may use — null = stay neutral. */
  ratedStatus: string | null;
  /** Whether the page should keep re-probing on the warming cadence. */
  shouldPoll: boolean;
}

/**
 * Single source of truth for the MCP card: display, banner rating and whether to keep
 * polling. Pure so it can be tested without a DOM (the web suite runs on node).
 *
 * A cold start must never colour the overall banner — scaling from zero is expected
 * behaviour, not a degraded platform — so "warming" rates as null exactly the way
 * "unknown" already does. Only once the re-polls are exhausted does it become a real
 * warning, because by then the container genuinely is not coming up.
 */
export function resolveMcpCardState(args: {
  check: HealthCheck | null;
  loading: boolean;
  attempts: number;
}): McpCardState {
  const { check, loading, attempts } = args;

  if (check?.status === "warming") {
    const exhausted = attempts >= MCP_WARMING_MAX_ATTEMPTS;
    if (exhausted) {
      return {
        display: {
          ...MCP_CARD_BASE,
          status: "warning",
          message: `Instance did not start within ~${Math.round((MCP_WARMING_MAX_ATTEMPTS * MCP_WARMING_POLL_MS) / 1000)}s — use Re-check.`,
        },
        ratedStatus: "warning",
        shouldPoll: false,
      };
    }
    // Deliberately keeps showing "warming" while the next probe is in flight: switching to
    // "checking" every 5s would make the card flicker for the whole cold start.
    //
    // The card owns this wording and ignores check.message on purpose: a scale-from-zero is
    // routine, nobody watching the page needs the probe budget or the mechanics of it (they
    // stay in the backend log and in the API message), and the two warming sources — the
    // server's own "warming" and a client-side probe timeout — must read identically.
    return {
      display: {
        ...MCP_CARD_BASE,
        status: "warming",
        message: `Starting instance… (attempt ${attempts + 1} of ${MCP_WARMING_MAX_ATTEMPTS})`,
        details: check.details,
      },
      ratedStatus: null,
      shouldPoll: true,
    };
  }

  if (loading) {
    return {
      display: { ...MCP_CARD_BASE, status: "checking", message: "Probing MCP server…" },
      ratedStatus: null,
      shouldPoll: false,
    };
  }

  if (!check) {
    return {
      display: { ...MCP_CARD_BASE, status: "unknown", message: "Not checked yet" },
      ratedStatus: null,
      shouldPoll: false,
    };
  }

  return {
    display: check,
    ratedStatus: check.status === "unknown" ? null : check.status,
    shouldPoll: false,
  };
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
