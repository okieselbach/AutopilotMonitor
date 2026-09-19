"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { TokenExpiredError } from "@/lib/authenticatedFetch";
import { fetchJson } from "@/lib/apiClient";

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

/** The ops event types that make up one background run's lifecycle. */
export interface OpsRunLifecycle {
  category: string;
  started: string;
  completed: string;
  failed: string;
}

export type OpsRunStatus =
  | { kind: "loading" }
  | { kind: "active"; since: string; triggeredBy: string }
  | { kind: "failed"; at: string; message: string }
  | { kind: "completed"; at: string; details: string | null }
  | { kind: "none" }; // no lifecycle events in the window

export function parseOpsDetails<T>(raw: string | null): T {
  if (!raw) return {} as T;
  try {
    return JSON.parse(raw) as T;
  } catch {
    return {} as T;
  }
}

/**
 * Status of a background run that reports through ops events (the trigger answers 202, there
 * is no job row): the latest Started newer than the latest terminal event (Completed/Failed)
 * means a run is active; otherwise the latest terminal event is the last run's outcome.
 * `lifecycle` must be a stable reference (module constant). With `pollWhileActiveMs` the
 * status reloads on that cadence for as long as a run is active.
 */
export function useOpsRunStatus(
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  setError: (error: string | null) => void,
  lifecycle: OpsRunLifecycle,
  pollWhileActiveMs?: number,
): { status: OpsRunStatus; refresh: () => void } {
  const [status, setStatus] = useState<OpsRunStatus>({ kind: "loading" });
  const [refreshKey, setRefreshKey] = useState(0);

  const fetchStatus = useCallback(async () => {
    try {
      const data = await fetchJson<OpsEventsResponse>(
        api.opsEvents.list(lifecycle.category, { pageSize: 100 }),
        getAccessToken,
      );

      const events = [...(data.events ?? [])];
      // Backend returns newest-first; keep it defensive anyway.
      events.sort((a, b) => (a.timestamp < b.timestamp ? 1 : -1));

      const started = events.find((e) => e.eventType === lifecycle.started);
      const terminal = events.find(
        (e) => e.eventType === lifecycle.completed || e.eventType === lifecycle.failed,
      );

      if (started && (!terminal || started.timestamp > terminal.timestamp)) {
        const details = parseOpsDetails<{ triggeredBy?: string }>(started.details);
        setStatus({ kind: "active", since: started.timestamp, triggeredBy: details.triggeredBy ?? "unknown" });
        return;
      }

      if (!terminal) {
        setStatus({ kind: "none" });
        return;
      }

      setStatus(
        terminal.eventType === lifecycle.failed
          ? { kind: "failed", at: terminal.timestamp, message: terminal.message }
          : { kind: "completed", at: terminal.timestamp, details: terminal.details },
      );
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      }
      // Non-auth failures degrade silently — the run status is auxiliary to the page.
      setStatus({ kind: "none" });
    }
  }, [getAccessToken, setError, lifecycle]);

  useEffect(() => {
    const run = async () => {
      await fetchStatus();
    };
    void run();
  }, [fetchStatus, refreshKey]);

  const active = status.kind === "active";
  useEffect(() => {
    if (!active || !pollWhileActiveMs) return;
    const timer = setInterval(() => setRefreshKey((k) => k + 1), pollWhileActiveMs);
    return () => clearInterval(timer);
  }, [active, pollWhileActiveMs]);

  const refresh = useCallback(() => setRefreshKey((k) => k + 1), []);
  return { status, refresh };
}
