"use client";

import { useCallback, useState } from "react";
import { api } from "@/lib/api";
import { ApiError, apiErrorText, fetchOk } from "@/lib/apiClient";
import { parseOpsDetails, useOpsRunStatus, type OpsRunLifecycle } from "@/hooks/useOpsRunStatus";

interface CompletedDetails {
  tenantsProcessed?: number;
  sessionsEnqueued?: number;
  durationMs?: number;
  abortedByBudget?: boolean;
  abortedByKillSwitch?: boolean;
}

type BannerState =
  | { kind: "loading" }
  | { kind: "active"; since: string; triggeredBy: string }
  | { kind: "budget-exceeded"; at: string; details: CompletedDetails }
  | { kind: "failed"; at: string; message: string }
  | { kind: "idle"; at: string; details: CompletedDetails }
  | { kind: "none" }; // no lifecycle events in the window — render nothing

const LIFECYCLE: OpsRunLifecycle = {
  category: "Maintenance",
  started: "SessionDeletionMaintenanceStarted",
  completed: "SessionDeletionMaintenanceCompleted",
  failed: "SessionDeletionMaintenanceFailed",
};

/**
 * Status banner for the session-deletion maintenance run (12h retention fanout + GC sweeps).
 * Derives its state purely from the SessionDeletionMaintenance* ops events: the latest
 * Started newer than the latest terminal event (Completed/Failed) means a run is active;
 * otherwise the latest terminal event decides between budget-exceeded / failed / idle.
 * The "Run now" button POSTs the manual trigger (202 = queued, 409 = already active).
 */
export function MaintenanceStatusBanner({
  getAccessToken,
  setError,
  setSuccessMessage,
}: {
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}) {
  const { status, refresh } = useOpsRunStatus(getAccessToken, setError, LIFECYCLE);
  const [triggering, setTriggering] = useState(false);

  let state: BannerState;
  if (status.kind === "completed") {
    const details = parseOpsDetails<CompletedDetails>(status.details);
    state = details.abortedByBudget
      ? { kind: "budget-exceeded", at: status.at, details }
      : { kind: "idle", at: status.at, details };
  } else {
    state = status;
  }

  const triggerRun = useCallback(async () => {
    setTriggering(true);
    try {
      // 202 = queued; 409 = a run is already active (not an error for the operator).
      const queued = await fetchOk(api.sessionDeletions.triggerMaintenance(), getAccessToken, { method: "POST" })
        .then(() => true)
        .catch((err: unknown) => {
          if (err instanceof ApiError && err.status === 409) return false;
          throw err;
        });
      setSuccessMessage(queued ? "Maintenance run queued — it will appear here as active shortly." : "A maintenance run is already active.");
      refresh();
    } catch (err) {
      setError(apiErrorText(err));
    } finally {
      setTriggering(false);
    }
  }, [getAccessToken, setError, setSuccessMessage, refresh]);

  if (state.kind === "loading" || state.kind === "none") return null;

  const runNowButton = (
    <button
      onClick={() => void triggerRun()}
      disabled={triggering}
      className="shrink-0 px-3 py-1.5 text-xs font-medium bg-purple-600 text-white rounded hover:bg-purple-700 disabled:opacity-50"
    >
      {triggering ? "Queuing…" : "Run now"}
    </button>
  );

  if (state.kind === "active") {
    return (
      <div className="flex items-center justify-between gap-4 rounded-md border border-purple-200 dark:border-purple-800 bg-purple-50 dark:bg-purple-900/20 px-4 py-3">
        <p className="text-sm text-purple-800 dark:text-purple-200">
          <span className="font-medium">Maintenance run active</span> — started{" "}
          {new Date(state.since).toLocaleString()} (triggered by {state.triggeredBy}). Retention
          deletions enqueued by this run appear under In-Flight.
        </p>
        <button
          onClick={refresh}
          className="shrink-0 px-3 py-1.5 text-xs font-medium border border-purple-300 dark:border-purple-700 text-purple-700 dark:text-purple-200 rounded hover:bg-purple-100 dark:hover:bg-purple-900/40"
        >
          Refresh
        </button>
      </div>
    );
  }

  if (state.kind === "budget-exceeded") {
    return (
      <div className="flex items-center justify-between gap-4 rounded-md border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-900/20 px-4 py-3">
        <p className="text-sm text-amber-800 dark:text-amber-200">
          <span className="font-medium">Last run stopped at the 50-minute budget</span> (
          {new Date(state.at).toLocaleString()}) after processing {state.details.tenantsProcessed ?? 0}{" "}
          tenants ({state.details.sessionsEnqueued ?? 0} sessions enqueued). The remaining backlog
          resumes with the next scheduled run (every 12 h at 00:00 / 12:00 UTC) — or trigger a run
          now.
        </p>
        {runNowButton}
      </div>
    );
  }

  if (state.kind === "failed") {
    return (
      <div className="flex items-center justify-between gap-4 rounded-md border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-900/20 px-4 py-3">
        <p className="text-sm text-red-800 dark:text-red-200">
          <span className="font-medium">Last maintenance run failed</span> (
          {new Date(state.at).toLocaleString()}): {state.message}
        </p>
        {runNowButton}
      </div>
    );
  }

  // idle — subtle last-run summary
  return (
    <div className="flex items-center justify-between gap-4 rounded-md border border-gray-200 dark:border-gray-700 bg-white dark:bg-gray-800 px-4 py-3">
      <p className="text-sm text-gray-600 dark:text-gray-400">
        Last maintenance run completed {new Date(state.at).toLocaleString()} — tenants=
        {state.details.tenantsProcessed ?? 0}, enqueued={state.details.sessionsEnqueued ?? 0}
        {typeof state.details.durationMs === "number"
          ? `, ${Math.round(state.details.durationMs / 60000)} min`
          : ""}
        . Next scheduled run: every 12 h at 00:00 / 12:00 UTC.
      </p>
      {runNowButton}
    </div>
  );
}
