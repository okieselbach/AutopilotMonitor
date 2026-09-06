"use client";

import { useEffect, useState } from "react";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { scopedApi } from "@/lib/scopedApi";
import VulnerabilityExposurePanel from "@/components/VulnerabilityExposurePanel";
import type { CveExposureSummary } from "@/utils/wire-types.generated";
import type { SoftwareTabScope, TimeRange } from "./types";
import { rangeToDays } from "./types";
import { ApiError, fetchJson } from "@/lib/apiClient";
import { notifyApiError } from "@/contexts/NotificationContext";

const TOP_N = 20;

export default function VulnerabilitiesTab({ scope, timeRange }: { scope: SoftwareTabScope; timeRange: TimeRange }) {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
    const { isGlobalAdmin, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const [summary, setSummary] = useState<CveExposureSummary | null>(null);
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
        const url = scopedApi.vulnerability(scope, days, TOP_N);
        const summary = await fetchJson<CveExposureSummary>(url, getAccessToken);
        if (cancelled) return;
        setSummary(summary);
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError) {
          notifyApiError(addNotification, "Backend Error", err, "vuln-exposure-error", "Failed to load vulnerability exposure.");
        } else {
          console.error("Failed to fetch vulnerability exposure", err);
          notifyApiError(addNotification, "Backend Not Reachable", err, "vuln-exposure-error", "Unable to load vulnerability exposure.");
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
