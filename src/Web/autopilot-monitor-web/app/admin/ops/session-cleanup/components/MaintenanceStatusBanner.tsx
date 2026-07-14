"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface OpsEvent {
  id: string;
  eventType: string;
  severity: string;
  message: string;
  details: string | null;
  timestamp: string;
}

interface OpsEventsResponse {
  events: OpsEvent[];
}

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

function parseDetails<T>(raw: string | null): T {
  if (!raw) return {} as T;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return {} as T;
  }
}

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
  const [state, setState] = useState<BannerState>({ kind: "loading" });
  const [triggering, setTriggering] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);

  const fetchStatus = useCallback(async () => {
    try {
      const r = await authenticatedFetch(
        api.opsEvents.list("Maintenance", { pageSize: 100 }),
        getAccessToken,
      );
      if (!r.ok) throw new Error(`HTTP ${r.status} loading maintenance ops events`);
      const data = (await r.json()) as OpsEventsResponse;

      const lifecycle = (data.events ?? []).filter((e) =>
        e.eventType.startsWith("SessionDeletionMaintenance"),
      );
      // Backend returns newest-first; keep it defensive anyway.
      lifecycle.sort((a, b) => (a.timestamp < b.timestamp ? 1 : -1));

      const started = lifecycle.find((e) => e.eventType === "SessionDeletionMaintenanceStarted");
      const terminal = lifecycle.find(
        (e) =>
          e.eventType === "SessionDeletionMaintenanceCompleted" ||
          e.eventType === "SessionDeletionMaintenanceFailed",
      );

      if (started && (!terminal || started.timestamp > terminal.timestamp)) {
        const details = parseDetails<{ triggeredBy?: string }>(started.details);
        setState({
          kind: "active",
          since: started.timestamp,
          triggeredBy: details.triggeredBy ?? "unknown",
        });
        return;
      }

      if (!terminal) {
        setState({ kind: "none" });
        return;
      }

      if (terminal.eventType === "SessionDeletionMaintenanceFailed") {
        setState({ kind: "failed", at: terminal.timestamp, message: terminal.message });
        return;
      }

      const details = parseDetails<CompletedDetails>(terminal.details);
      setState(
        details.abortedByBudget
          ? { kind: "budget-exceeded", at: terminal.timestamp, details }
          : { kind: "idle", at: terminal.timestamp, details },
      );
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      }
      // Non-auth failures degrade silently — the banner is auxiliary to the tabs.
      setState({ kind: "none" });
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    void fetchStatus();
  }, [fetchStatus, refreshKey]);

  const triggerRun = useCallback(async () => {
    setTriggering(true);
    try {
      const r = await authenticatedFetch(api.sessionDeletions.triggerMaintenance(), getAccessToken, {
        method: "POST",
      });
      if (r.status === 202) {
        setSuccessMessage("Maintenance run queued — it will appear here as active shortly.");
      } else if (r.status === 409) {
        setSuccessMessage("A maintenance run is already active.");
      } else {
        const body = await r.text();
        throw new Error(`HTTP ${r.status}: ${body}`);
      }
      setRefreshKey((k) => k + 1);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setTriggering(false);
    }
  }, [getAccessToken, setError, setSuccessMessage]);

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
          onClick={() => setRefreshKey((k) => k + 1)}
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
