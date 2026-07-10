"use client";

import { useEffect, useState } from "react";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import VulnerabilityExposurePanel, { type VulnerabilitySummary } from "@/components/VulnerabilityExposurePanel";
import type { SoftwareTabScope, TimeRange } from "./types";
import { rangeToDays } from "./types";

const TOP_N = 20;

export default function VulnerabilitiesTab({ scope, timeRange }: { scope: SoftwareTabScope; timeRange: TimeRange }) {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const { isGlobalAdmin, routeGlobal, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const [summary, setSummary] = useState<VulnerabilitySummary | null>(null);
  const [loading, setLoading] = useState(true);

  // GA without a specific tenant selected sees the cross-tenant aggregate (incl. affected tenants).
  const showTenantCount = isGlobalAdmin && !selectedTenantId;

  useEffect(() => {
    if (!scopeInitialized) return;
    let cancelled = false;
    const days = rangeToDays(timeRange);

    const run = async () => {
      try {
        setLoading(true);
        const url = routeGlobal
          ? api.metrics.globalVulnerability(days, TOP_N, selectedTenantId || undefined)
          : api.metrics.vulnerability(days, TOP_N);
        const res = await authenticatedFetch(url, getAccessToken);
        if (cancelled) return;
        if (res.ok) {
          setSummary((await res.json()) as VulnerabilitySummary);
        } else {
          addNotification("error", "Backend Error", `Failed to load vulnerability exposure: ${res.statusText}`, "vuln-exposure-error");
        }
      } catch (err) {
        if (cancelled) return;
        if (err instanceof TokenExpiredError) {
          addNotification("error", "Session Expired", err.message, "session-expired-error");
        } else {
          console.error("Failed to fetch vulnerability exposure", err);
          addNotification("error", "Backend Not Reachable", "Unable to load vulnerability exposure.", "vuln-exposure-error");
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    };

    void run();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, scopeKey, timeRange]);

  return <VulnerabilityExposurePanel summary={summary} loading={loading} showTenantCount={showTenantCount} />;
}
