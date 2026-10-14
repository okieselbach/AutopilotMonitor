"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { fetchJson } from "@/lib/apiClient";
import { recentWindowStartIso, sumRecentFires } from "@/lib/ruleRecentFires";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import type { RuleStatsResponse } from "@/lib/generated/wire-types.generated";

export interface RuleFireStats {
  /** Share of the tenant's enrollments each rule fired on in 30 days; rules that fired only. */
  hitRates: Record<string, number>;
  /** Enrollments each rule fired on in the last 14 days; rules that fired only. */
  recentFires: Record<string, number>;
  /** The session belongs to another tenant than the viewer's: the stats and their links name that tenant. */
  crossTenant: boolean;
}

/**
 * Analyze-rule telemetry next to a session's findings, for the tenant the session belongs to: the
 * member route for the viewer's own tenant, the global route scoped to the session's tenant in a
 * cross-tenant view (GA, delegated drill-in), where a 403 leaves the stats empty rather than showing
 * the viewer's own numbers next to a foreign session. Fetched while the session has findings and again
 * when their number changes; fail-soft.
 */
export function useRuleFireStats(sessionTenantId: string | undefined, findingCount: number): RuleFireStats {
  const { getAccessToken } = useAuth();
  const { tenantId } = useTenant();
  const [stats, setStats] = useState<Omit<RuleFireStats, "crossTenant">>({ hitRates: {}, recentFires: {} });
  const crossTenant = !!sessionTenantId && !!tenantId && sessionTenantId.toLowerCase() !== tenantId.toLowerCase();

  useEffect(() => {
    if (!tenantId || findingCount === 0) return;
    let cancelled = false;
    const run = async () => {
      try {
        const url = crossTenant
          ? api.metrics.globalRuleStats(undefined, undefined, "analyze", sessionTenantId)
          : api.metrics.ruleStats(undefined, undefined, "analyze");
        const data = await fetchJson<RuleStatsResponse>(url, getAccessToken);
        const hitRates: Record<string, number> = {};
        const recentFires: Record<string, number> = {};
        const windowStart = recentWindowStartIso(new Date(), 14);
        for (const r of data.rules ?? []) {
          if (r.hitRate > 0) hitRates[r.ruleId] = r.hitRate;
          const recent = sumRecentFires(r.trend, windowStart);
          if (recent > 0) recentFires[r.ruleId] = recent;
        }
        if (!cancelled) setStats({ hitRates, recentFires });
      } catch {
        // Non-critical: no rule telemetry next to the findings.
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [tenantId, crossTenant, sessionTenantId, findingCount, getAccessToken]);

  return { ...stats, crossTenant };
}
