"use client";

import { useEffect, useMemo, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import type { EnrollmentEvent } from "@/types";

interface UseSessionAnchorEventsParams {
  sessionId: string;
  tenantId?: string;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}

export interface UseSessionAnchorEventsReturn {
  /** Anchor events (those carrying `data.decisionState`) in chronological order. */
  anchors: EnrollmentEvent[];
  /** Total count of events fetched (anchor + non-anchor). */
  totalEvents: number;
  loading: boolean;
  error: string | null;
  reload: () => void;
}

/**
 * Loads the session's events and filters to those carrying a `data.decisionState`
 * payload (Plan §A — Edge-Triggered State Snapshots, 2026-05-03). Sorted by
 * `sequence` so the operator reads the snapshots in the order they were emitted.
 *
 * The filter is intentionally schema-driven, not allowlist-driven: an event is
 * an "anchor" iff it actually carries a snapshot. Mirrors what the agent emitted
 * exactly, even if the C# allowlist evolves between agent versions.
 */
export function useSessionAnchorEvents({
  sessionId,
  tenantId,
  getAccessToken,
}: UseSessionAnchorEventsParams): UseSessionAnchorEventsReturn {
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [reloadCounter, setReloadCounter] = useState(0);

  useEffect(() => {
    if (!sessionId) return;
    let aborted = false;

    setLoading(true);
    setError(null);

    (async () => {
      try {
        const url = api.sessions.events(sessionId, tenantId);
        const response = await authenticatedFetch(url, getAccessToken);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}: ${response.statusText}`);
        }
        const data = await response.json();
        if (aborted) return;
        const fetched: EnrollmentEvent[] = Array.isArray(data.events) ? data.events : [];
        setEvents(fetched);
      } catch (err) {
        if (aborted) return;
        if (err instanceof TokenExpiredError) {
          setError("Session expired — please reload to sign in again.");
        } else {
          setError(err instanceof Error ? err.message : "Failed to load events");
        }
      } finally {
        if (!aborted) setLoading(false);
      }
    })();

    return () => {
      aborted = true;
    };
  }, [sessionId, tenantId, getAccessToken, reloadCounter]);

  const anchors = useMemo(
    () => filterAnchorEvents(events),
    [events],
  );

  return {
    anchors,
    totalEvents: events.length,
    loading,
    error,
    reload: () => setReloadCounter((n) => n + 1),
  };
}

/**
 * Schema-driven filter: an event is an "anchor" iff its `data.decisionState`
 * is a non-null object. Exported separately so the unit test can pin the
 * exact predicate without spinning up the hook.
 */
export function filterAnchorEvents(events: EnrollmentEvent[]): EnrollmentEvent[] {
  return events
    .filter((e) => isAnchorEvent(e))
    .sort((a, b) => (a.sequence ?? 0) - (b.sequence ?? 0));
}

export function isAnchorEvent(event: EnrollmentEvent): boolean {
  const ds = event.data?.decisionState;
  return typeof ds === "object" && ds !== null;
}
