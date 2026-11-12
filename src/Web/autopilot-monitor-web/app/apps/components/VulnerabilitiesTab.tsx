"use client";

import { useEffect, useState } from "react";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { scopedApi } from "@/lib/scopedApi";
import VulnerabilityExposurePanel from "@/components/VulnerabilityExposurePanel";
import { AnalyzerOffNotice } from "@/components/vulnerability/AnalyzerOffNotice";
import type { CveExposureSummary } from "@/lib/generated/wire-types.generated";
import type { SoftwareTabScope } from "./types";
import { windowProgressKey } from "@/lib/timeWindow";
import { ApiError, fetchJson } from "@/lib/apiClient";
import { notifyApiError } from "@/contexts/NotificationContext";
import { useFetchProgress } from "@/hooks/useFetchProgress";
import { trackEvent } from "@/lib/appInsights";
import type { ExposureAction } from "@/lib/vulnerabilityExposure";

const TOP_N = 20;
/** Per severity/priority band, its top entries outside the lists, so every band filter shows something. */
const PER_BAND = 10;

/** Counts a filter, lens, sort, expand or self-updating step; never a CVE id or a product name. */
function trackExposureAction({ action, value, active }: ExposureAction) {
  trackEvent("vuln_exposure_action", {
    action,
    ...(value !== undefined ? { value } : {}),
    ...(active !== undefined ? { active } : {}),
  });
}

/** A completed load: the summary with the window and scope it was read for. */
interface LoadedExposure {
  summary: CveExposureSummary;
  days: number;
  crossTenant: boolean;
}

export default function VulnerabilitiesTab({
  scope,
  days,
  windowReady,
  analyzerOn,
  canEnable,
}: {
  scope: SoftwareTabScope;
  days: number;
  windowReady: boolean;
  /** The tenant's analyzer switch: null while it loads, undefined in an aggregate view or when unreadable. */
  analyzerOn?: boolean | null;
  /** May turn the analyzer on from here (lib/tenantScope.ts canChangeOwnTenantSettings). */
  canEnable: boolean;
}) {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const { isGlobalAdmin, selectedTenantId, scopeInitialized, scopeKey } = scope;

  const [loaded, setLoaded] = useState<LoadedExposure | null>(null);
  const [loading, setLoading] = useState(true);
  const summary = loaded?.summary ?? null;
  // The panel's Show / Hide of self-updating software. A new object per click, so that the same
  // choice loads again after a load that failed and left the earlier numbers on screen.
  const [selfUpdating, setSelfUpdating] = useState({ include: false });

  // GA without a specific tenant selected sees the cross-tenant aggregate (incl. affected tenants).
  const showTenantCount = isGlobalAdmin && !selectedTenantId;

  // The cross-tenant scan takes far longer than one tenant's, so each keeps its own estimate.
  const progress = useFetchProgress(
    windowProgressKey(showTenantCount ? "vulnExposure.all.lastFetchMs" : "vulnExposure.tenant.lastFetchMs", days),
  );
  const { begin: progressBegin, finish: progressFinish } = progress;

  useEffect(() => {
    if (!scopeInitialized || !windowReady) return;
    let cancelled = false;

    const run = async () => {
      let succeeded = false;
      try {
        setLoading(true);
        progressBegin();
        const url = scopedApi.vulnerability(scope, days, TOP_N, PER_BAND, selfUpdating.include);
        const summary = await fetchJson<CveExposureSummary>(url, getAccessToken);
        if (cancelled) return;
        setLoaded({ summary, days, crossTenant: showTenantCount });
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
  }, [scopeInitialized, windowReady, scopeKey, days, selfUpdating]);

  const hasFindings = !!summary && summary.distinctCves > 0;
  const analyzerOff = analyzerOn === false;

  // What the tab showed, once per completed load and only after the analyzer switch is known, so an
  // empty tenant is told apart from one whose scanning is off.
  const viewState = !loaded || loading || analyzerOn === null
    ? null
    : loaded.summary.distinctCves > 0 ? "findings" : analyzerOff ? "analyzer_off" : "empty";
  useEffect(() => {
    if (!viewState || !loaded) return;
    trackEvent("vuln_exposure_viewed", {
      state: viewState,
      scope: loaded.crossTenant ? "global" : "tenant",
      days: loaded.days,
      cves: loaded.summary.distinctCves,
      kev: loaded.summary.kevCves,
      highRisk: loaded.summary.priorityBreakdown.attend,
      selfUpdating: loaded.summary.selfUpdatingIncluded ? "included" : "excluded",
    });
  }, [viewState, loaded]);

  return (
    <div className="space-y-4">
      {analyzerOff && hasFindings && (
        <p className="text-sm text-gray-500">
          Scanning is off for this tenant: these findings are from before, new enrollments are not scanned.
        </p>
      )}
      <VulnerabilityExposurePanel
        summary={summary}
        loading={loading}
        showTenantCount={showTenantCount}
        progress={{ elapsedMs: progress.elapsedMs, estimateMs: progress.estimateMs }}
        emptyState={analyzerOff ? <AnalyzerOffNotice subject="vulnerabilities" canEnable={canEnable} /> : undefined}
        onAction={trackExposureAction}
        onIncludeSelfUpdatingChange={(include) => setSelfUpdating({ include })}
      />
    </div>
  );
}
