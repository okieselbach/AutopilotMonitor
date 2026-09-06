"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { createBurstScheduler, type BurstScheduler } from "@/lib/burstScheduler";
import { EnrollmentEvent, Session } from "@/types";
import { type NotificationType, notifyApiError } from "@/contexts/NotificationContext";
import type { ProgressGetSessionEventsResponse, ProgressLookupSessionResponse } from "@/utils/wire-types.generated";
import { fetchJson, nullOnApiError } from "@/lib/apiClient";

// Live refetch coalescing on SignalR signals (see lib/burstScheduler). Each refresh is two
// requests (summary lookup + events), hence a slightly wider window than the session page.
const REFRESH_TRAILING_MS = 500;
const REFRESH_MAX_WAIT_MS = 1_500;

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface UseProgressEventsParams {
  session: Session | null;
  setSession: React.Dispatch<React.SetStateAction<Session | null>>;
  tenantId: string;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
}

export interface UseProgressEventsReturn {
  events: EnrollmentEvent[];
  setEvents: React.Dispatch<React.SetStateAction<EnrollmentEvent[]>>;
  sessionRef: React.RefObject<Session | null>;
  /** Coalesced live refetch — call on every SignalR signal, the scheduler decides when to fetch. */
  scheduleFetchEvents: () => void;
}

/**
 * Owns the progress page's event list lifecycle:
 *  - keeps a sessionRef in sync (used by SignalR hook + coalesced refetch)
 *  - initial event fetch once per session (StrictMode-safe guard)
 *  - burst-coalesced scheduleFetchEvents that refreshes session summary + events
 *  - merge-by-sequence dedup so repeat signals don't duplicate rows
 */
export function useProgressEvents({
  session,
  setSession,
  tenantId,
  getAccessToken,
  addNotification,
}: UseProgressEventsParams): UseProgressEventsReturn {
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const sessionRef = useRef<Session | null>(null);
  const lastFetchedSessionId = useRef<string | null>(null);
  // The scheduler is created lazily on the first signal (refs stay out of render) and
  // runs through refreshRef so it always sees the latest closure.
  const refreshRef = useRef<() => Promise<void>>(async () => {});
  const refreshScheduler = useRef<BurstScheduler | null>(null);

  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  // Reset per-session fetch guard + events when the selected session changes
  useEffect(() => {
    const run = async () => {
      if (!session) {
        lastFetchedSessionId.current = null;
        setEvents([]);
        return;
      }
      if (lastFetchedSessionId.current === session.sessionId) return;
      lastFetchedSessionId.current = session.sessionId;
      setEvents([]);

      const fetchEvents = async () => {
        try {
          const data = await fetchJson<ProgressGetSessionEventsResponse>(
            api.progress.sessionEvents(session.sessionId, tenantId, session.serialNumber),
            getAccessToken,
          );
          const fetched: EnrollmentEvent[] = data.events || [];
          setEvents((prev) => {
            if (prev.length === 0) return fetched;
            const existingIds = new Set(prev.map((e) => e.eventId));
            const newEvents = fetched.filter((e) => !existingIds.has(e.eventId));
            if (newEvents.length === 0) return prev;
            return [...prev, ...newEvents].sort(
              (a, b) => a.sequence - b.sequence,
            );
          });
        } catch (error) {
          console.error("Failed to fetch events:", error);
          notifyApiError(addNotification, "Backend Error", error, "progress-events-error", "Unable to load enrollment events.");
        }
      };
      await fetchEvents();
    };
    void run();
  }, [session, tenantId, getAccessToken, addNotification]);

  const refresh = useCallback(
    async () => {
      const currentSession = sessionRef.current;
      if (!currentSession) return;
      try {
        // Refresh the session summary via the serial lookup (the canonical serial from the
        // session row always matches exactly). Guard on sessionId: the lookup returns the
        // NEWEST session for the serial, which after a re-enrollment is a different session —
        // the page must keep showing the one the user selected.
        const lookupData = await fetchJson<ProgressLookupSessionResponse>(
          api.progress.lookup(tenantId, currentSession.serialNumber),
          getAccessToken,
        ).catch(nullOnApiError);
        const updated = lookupData?.found ? lookupData.session ?? null : null;
        if (updated && updated.sessionId === currentSession.sessionId) {
          setSession(updated);
        }

        const eventsData = await fetchJson<ProgressGetSessionEventsResponse>(
          api.progress.sessionEvents(currentSession.sessionId, tenantId, currentSession.serialNumber),
          getAccessToken,
        );
        const fetched: EnrollmentEvent[] = eventsData.events || [];
        setEvents((prev) => {
          const existingIds = new Set(prev.map((e) => e.eventId));
          const newEvents = fetched.filter((e) => !existingIds.has(e.eventId));
          if (newEvents.length === 0) return prev;
          return [...prev, ...newEvents].sort(
            (a, b) => a.sequence - b.sequence,
          );
        });
      } catch (error) {
        console.error("[Progress] Refetch failed:", error);
      }
    },
    [tenantId, getAccessToken, setSession],
  );

  useEffect(() => {
    refreshRef.current = refresh;
  }, [refresh]);

  const scheduleFetchEvents = useCallback(() => {
    refreshScheduler.current ??= createBurstScheduler(
      () => { void refreshRef.current(); },
      { trailingMs: REFRESH_TRAILING_MS, maxWaitMs: REFRESH_MAX_WAIT_MS },
    );
    refreshScheduler.current.trigger();
  }, []);

  // Drop a pending trailing refetch on unmount
  useEffect(() => {
    return () => refreshScheduler.current?.cancel();
  }, []);

  return {
    events,
    setEvents,
    sessionRef,
    scheduleFetchEvents,
  };
}
