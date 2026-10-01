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
import { useFetchProgress } from "@/hooks/useFetchProgress";

const TOP_N = 20;
/** Per severity/priority band, its top entries outside the lists, so every band filter shows something. */
const PER_BAND = 10;

export default function VulnerabilitiesTab({ scope, timeRange }: { scope: SoftwareTabScope; timeRange: TimeRange }) {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
    const { isGlobalAdmin, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const [summary, setSummary] = useState<CveExposureSummary | null>(null);
  const [loading, setLoading] = useState(true);

  // GA without a specific tenant selected sees the cross-tenant aggregate (incl. affected tenants).
  const showTenantCount = isGlobalAdmin && !selectedTenantId;

  // The cross-tenant scan takes far longer than one tenant's, so each keeps its own estimate.
  const progress = useFetchProgress(showTenantCount ? "vulnExposure.all.lastFetchMs" : "vulnExposure.tenant.lastFetchMs");
  const { begin: progressBegin, finish: progressFinish } = progress;

  useEffect(() => {
    if (!scopeInitialized) return;
    let cancelled = false;
    const days = rangeToDays(timeRange);

    const run = async () => {
      let succeeded = false;
      try {
        setLoading(true);
        progressBegin();
        const url = scopedApi.vulnerability(scope, days, TOP_N, PER_BAND);
        const summary = await fetchJson<CveExposureSummary>(url, getAccessToken);
        if (cancelled) return;
        setSummary(summary);
        succeeded = true;
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError) {
          notifyApiError(addNotification, "Backend Error", err, "vuln-exposure-error", "Failed to load vulnerability exposure.");
        } else {
          console.error("Failed to fetch vulnerability exposure", err);
          notifyApiError(addNotification, "Backend Not Reachable", err, "vuln-exposure-error", "Unable to load vulnerability exposure.");
        }
      } finally {
        // A cancelled run leaves the progress to the run that replaced it.
        if (!cancelled) {
          progressFinish(succeeded);
          setLoading(false);
        }
      }
    };

    void run();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [scopeInitialized, scopeKey, timeRange]);

  return (
    <VulnerabilityExposurePanel
      summary={summary}
      loading={loading}
      showTenantCount={showTenantCount}
      progress={{ elapsedMs: progress.elapsedMs, estimateMs: progress.estimateMs }}
    />
  );
}
