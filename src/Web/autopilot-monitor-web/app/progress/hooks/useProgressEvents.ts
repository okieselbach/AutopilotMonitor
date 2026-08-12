"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { EnrollmentEvent, Session } from "@/types";
import type { NotificationType } from "@/contexts/NotificationContext";

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
  scheduleFetchEvents: (delayMs?: number) => void;
}

/**
 * Owns the progress page's event list lifecycle:
 *  - keeps a sessionRef in sync (used by SignalR hook + debounced refetch)
 *  - initial event fetch once per session (StrictMode-safe guard)
 *  - debounced scheduleFetchEvents that refreshes session summary + events
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
  const refetchTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

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
          const response = await authenticatedFetch(
            api.progress.sessionEvents(session.sessionId, tenantId),
            getAccessToken,
          );
          if (response.ok) {
            const data = await response.json();
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
          } else {
            addNotification(
              "error",
              "Backend Error",
              `Failed to load enrollment events: ${response.statusText}`,
              "progress-events-error",
            );
          }
        } catch (error) {
          if (error instanceof TokenExpiredError) {
            addNotification(
              "error",
              "Session Expired",
              error.message,
              "session-expired-error",
            );
          } else {
            console.error("Failed to fetch events:", error);
            addNotification(
              "error",
              "Backend Not Reachable",
              "Unable to load enrollment events. Please check your connection.",
              "progress-events-error",
            );
          }
        }
      };
      await fetchEvents();
    };
    void run();
  }, [session, tenantId, getAccessToken, addNotification]);

  const scheduleFetchEvents = useCallback(
    (delayMs: number = 500) => {
      if (refetchTimerRef.current) clearTimeout(refetchTimerRef.current);
      refetchTimerRef.current = setTimeout(async () => {
        const currentSession = sessionRef.current;
        if (!currentSession) return;
        try {
          const sessionsResponse = await authenticatedFetch(
            api.progress.sessions(tenantId),
            getAccessToken,
          );
          if (sessionsResponse.ok) {
            const sessionsData = await sessionsResponse.json();
            const sessions: Session[] = sessionsData.sessions || [];
            const updated = sessions.find(
              (s) => s.sessionId === currentSession.sessionId,
            );
            if (updated) setSession(updated);
          }

          const eventsResponse = await authenticatedFetch(
            api.progress.sessionEvents(currentSession.sessionId, tenantId),
            getAccessToken,
          );
          if (eventsResponse.ok) {
            const eventsData = await eventsResponse.json();
            const fetched: EnrollmentEvent[] = eventsData.events || [];
            setEvents((prev) => {
              const existingIds = new Set(prev.map((e) => e.eventId));
              const newEvents = fetched.filter((e) => !existingIds.has(e.eventId));
              if (newEvents.length === 0) return prev;
              return [...prev, ...newEvents].sort(
                (a, b) => a.sequence - b.sequence,
              );
            });
          }
        } catch (error) {
          console.error("[Progress] Refetch failed:", error);
        }
      }, delayMs);
    },
    [tenantId, getAccessToken, setSession],
  );

  useEffect(() => {
    return () => {
      if (refetchTimerRef.current) clearTimeout(refetchTimerRef.current);
    };
  }, []);

  return {
    events,
    setEvents,
    sessionRef,
    scheduleFetchEvents,
  };
}
