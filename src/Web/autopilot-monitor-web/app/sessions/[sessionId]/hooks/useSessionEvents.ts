"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { extractContinuation, MAX_EAGER_PAGES } from "@/lib/paginationLink";
import { isGuid } from "@/utils/inputValidation";
import { isTerminalStatus } from "@/utils/sessionStatus";
import { EnrollmentEvent, Session } from "@/types";
import type { NotificationType } from "@/contexts/NotificationContext";

const TIMELINE_PAGE_SIZE = 200;

// Single-shot refetch delay after the session transitions to a terminal status.
// EnrollmentTerminationHandler emits trailing events (enrollment_summary_shown,
// app_tracking_summary, diagnostics_collecting, diagnostics_uploaded) within ~5-10s
// after enrollment_complete. The post-terminal SignalR/polling gate would otherwise
// drop them until the user manually refreshes. One nachfass-fetch covers the window.
const TERMINAL_TRAILING_REFETCH_DELAY_MS = 12_000;

/**
 * Stable event key — mirrors the React `key` used by the timeline rows so that
 * merge identity and React reconciliation identity agree.
 */
function eventKey(e: EnrollmentEvent): string {
  return e.eventId || `${e.sessionId}-${e.sequence}`;
}

/**
 * Append-only merge: returns `prev` plus any events from `incoming` whose key is
 * not already present. Session telemetry is append-only (events are immutable
 * once written), so existing rows are never replaced. The timeline relies on two
 * consequences of this:
 *   - The rendered list is monotonic — it never shrinks mid-refresh. The
 *     progressive paging fetch used to wipe the list to the first 200 rows and
 *     then grow it back as later pages streamed in, which changed the page
 *     height repeatedly and made the scrollbar/viewport jump while reading.
 *   - When a refresh brings nothing new, the SAME array reference is returned,
 *     so React bails out of the re-render entirely — open "Details" panels and
 *     scroll position stay exactly where the user left them.
 * New events arrive at the bottom (highest Sequence) and the render-time sort in
 * useSessionDerivedData keeps ordering correct regardless of append order, so
 * the browser's native scroll anchoring keeps the viewport pinned.
 */
export function mergeNewEvents(prev: EnrollmentEvent[], incoming: EnrollmentEvent[]): EnrollmentEvent[] {
  if (incoming.length === 0) return prev;
  const seen = new Set(prev.map(eventKey));
  const additions = incoming.filter(e => !seen.has(eventKey(e)));
  if (additions.length === 0) return prev;
  return prev.concat(additions);
}

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface UseSessionEventsParams {
  sessionId: string;
  sessionTenantId: string | null;
  sessionStatus: string | null | undefined;
  resolveEffectiveTenantId: () => string | null;
  sessionRef: React.MutableRefObject<Session | null>;
  fetchSessionDetails: () => Promise<void>;
  setLoading: React.Dispatch<React.SetStateAction<boolean>>;
  isConnected: boolean;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
}

export interface UseSessionEventsReturn {
  events: EnrollmentEvent[];
  setEvents: React.Dispatch<React.SetStateAction<EnrollmentEvent[]>>;
  fetchEvents: () => Promise<void>;
  scheduleFetchEvents: (delayMs?: number) => void;
  /**
   * True while a Pattern-A eager-fetch is still streaming pages after the first
   * batch has rendered. Surfaces a "loading more events…" indicator on the
   * timeline page without delaying first paint.
   */
  isStreamingMore: boolean;
}

/**
 * Owns the session detail page's event list lifecycle:
 *  - fetch events from Table Storage (canonical truth)
 *  - in-flight dedup (SignalR + 30s timer + group-join can overlap)
 *  - debounced scheduleFetchEvents to absorb bursts
 *  - empty-refresh guard: ignores transient empty lists, keeps last known-good
 *  - terminal-event detection: triggers session refetch if SignalR status delta was lost
 *  - triggers initial fetch once sessionTenantId is known
 *  - 30s fallback polling while SignalR disconnected (visible tab only)
 */
export function useSessionEvents({
  sessionId,
  sessionTenantId,
  sessionStatus,
  resolveEffectiveTenantId,
  sessionRef,
  fetchSessionDetails,
  setLoading,
  isConnected,
  getAccessToken,
  addNotification,
}: UseSessionEventsParams): UseSessionEventsReturn {
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [isStreamingMore, setIsStreamingMore] = useState(false);

  // Debounce real-time event refreshes to avoid burst reads in Table Storage.
  const eventRefreshTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  // Deduplication: track in-flight fetchEvents to avoid concurrent calls
  const fetchEventsInFlight = useRef(false);
  const fetchEventsQueued = useRef(false);

  const fetchEvents = useCallback(async () => {
    // Deduplication: if a fetch is already in flight, queue one follow-up instead of
    // stacking concurrent requests (SignalR signal + 30s timer + group-join can overlap).
    if (fetchEventsInFlight.current) {
      fetchEventsQueued.current = true;
      return;
    }
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!effectiveTenantId || !isGuid(effectiveTenantId)) {
      return;
    }
    fetchEventsInFlight.current = true;
    fetchEventsQueued.current = false;

    // Pattern A — progressive eager fetch:
    // 1. Fetch the first page (pageSize=200), render immediately so the user sees
    //    the timeline populate within milliseconds even on huge sessions.
    // 2. If a nextLink is returned, keep fetching pages in the background and
    //    append to the rendered list. Surface isStreamingMore so the page can
    //    show a small "loading more events…" indicator while batches stream.
    // 3. The merged list is sorted by Sequence at render time (eventsByPhase
    //    useMemo) — cross-page Azure-Tables row-key order is therefore irrelevant.
    let aborted = false;
    try {
      const firstPageUrl = api.sessions.events(sessionId, effectiveTenantId, {
        pageSize: TIMELINE_PAGE_SIZE,
      });
      const firstResponse = await authenticatedFetch(firstPageUrl, getAccessToken);
      if (!firstResponse.ok) {
        addNotification('error', 'Backend Error', `Failed to load session events: ${firstResponse.statusText}`, 'session-events-fetch-error');
        return;
      }
      const firstData = await firstResponse.json();
      const firstBatch: EnrollmentEvent[] = Array.isArray(firstData.events) ? firstData.events : [];

      // Append-only merge instead of replace: the list stays monotonic so it
      // never shrinks-then-grows across the paged refresh (the old wipe-to-200
      // behaviour made the scrollbar/viewport jump on every poll). A transient
      // empty refresh is also naturally absorbed — mergeNewEvents returns the
      // previous list unchanged when nothing new arrived.
      setEvents(prev => mergeNewEvents(prev, firstBatch));

      // Trigger an early loading=false now: first paint is unblocked.
      setLoading(false);

      // Surgical status-stale detection: if any event in the merged stream is
      // terminal but the session is still InProgress, the SignalR status delta
      // was likely lost — refetch session details. We check incrementally
      // across every page (not only firstBatch) because for sessions with
      // >TIMELINE_PAGE_SIZE events the terminal event lives on page 2+, and a
      // first-batch-only check would miss it entirely.
      const isTerminalEvent = (e: EnrollmentEvent) =>
        e.eventType === "enrollment_complete" ||
        e.eventType === "enrollment_failed" ||
        // Server-authored terminal: the maintenance sweep marks the session Failed and emits this
        // in one pass, but sends no SignalR delta — so a live viewer's status can lag until refetch.
        e.eventType === "session_timeout";
      let foundTerminalEvent = firstBatch.some(isTerminalEvent);

      let continuation = extractContinuation(firstData.nextLink);
      if (continuation) {
        setIsStreamingMore(true);
        let pagesFetched = 1;
        while (continuation && pagesFetched < MAX_EAGER_PAGES) {
          const url = api.sessions.events(sessionId, effectiveTenantId, {
            pageSize: TIMELINE_PAGE_SIZE,
            continuation,
          });
          const resp = await authenticatedFetch(url, getAccessToken);
          if (!resp.ok) {
            console.warn(
              `[SessionDetail] eager-fetch stopped at page ${pagesFetched + 1} (status=${resp.status})`,
            );
            break;
          }
          const pageData = await resp.json();
          const batch: EnrollmentEvent[] = Array.isArray(pageData.events) ? pageData.events : [];
          if (batch.length > 0) {
            setEvents(prev => mergeNewEvents(prev, batch));
            if (!foundTerminalEvent && batch.some(isTerminalEvent)) {
              foundTerminalEvent = true;
            }
          }
          continuation = extractContinuation(pageData.nextLink);
          pagesFetched++;
        }
        if (pagesFetched >= MAX_EAGER_PAGES) {
          console.warn(
            `[SessionDetail] eager-fetch hit MAX_EAGER_PAGES=${MAX_EAGER_PAGES}; remaining events not loaded`,
          );
        }
        setIsStreamingMore(false);
      }

      const currentStatus = sessionRef.current?.status;
      if (foundTerminalEvent && currentStatus && !isTerminalStatus(currentStatus)) {
        console.info(
          `[SessionDetail] Terminal event detected but session status is '${currentStatus}' — refetching session details`
        );
        fetchSessionDetails();
      }
    } catch (error) {
      aborted = true;
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch events:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load session events. Please check your connection.', 'session-events-fetch-error');
      }
    } finally {
      setLoading(false);
      if (aborted) setIsStreamingMore(false);
      fetchEventsInFlight.current = false;

      // If another fetch was requested while we were in flight, run it now
      if (fetchEventsQueued.current) {
        fetchEventsQueued.current = false;
        fetchEvents();
      }
    }
  }, [sessionId, resolveEffectiveTenantId, sessionRef, fetchSessionDetails, setLoading, getAccessToken, addNotification]);

  const scheduleFetchEvents = useCallback((delayMs = 300) => {
    if (eventRefreshTimeoutRef.current) {
      clearTimeout(eventRefreshTimeoutRef.current);
    }
    eventRefreshTimeoutRef.current = setTimeout(() => {
      fetchEvents();
    }, delayMs);
  }, [fetchEvents]);

  // Fetch events when we have the session's tenant ID
  useEffect(() => {
    if (sessionTenantId && sessionId) {
      fetchEvents();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionTenantId, sessionId]);

  // Clear debounce timer on unmount
  useEffect(() => {
    return () => {
      if (eventRefreshTimeoutRef.current) {
        clearTimeout(eventRefreshTimeoutRef.current);
      }
    };
  }, []);

  // One-shot trailing-events refetch on terminal transition.
  // Counterpart to the SignalR eventStream gate in useSessionSignalR: that gate
  // suppresses all post-terminal pushes to avoid perf/metrics-snapshot thrashing, but
  // EnrollmentTerminationHandler still emits real lifecycle events (enrollment_summary_shown,
  // app_tracking_summary, diagnostics_collecting, diagnostics_uploaded) ~5-10s after
  // enrollment_complete. We schedule exactly one nachfass-fetch to capture them, then stop.
  useEffect(() => {
    if (!isTerminalStatus(sessionStatus)) return;
    const timer = setTimeout(() => {
      if (document.visibilityState !== "visible") return;
      fetchEvents();
    }, TERMINAL_TRAILING_REFETCH_DELAY_MS);
    return () => clearTimeout(timer);
  }, [sessionStatus, fetchEvents]);

  // Fallback polling only while SignalR is disconnected.
  // Symmetric with the SignalR eventStream gate: once the session has reached a terminal
  // status, suppress the 30s refetch loop too — late agent perf/metrics snapshots would
  // otherwise still trigger refetches, defeating the SignalR-side gate.
  useEffect(() => {
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!sessionId || !effectiveTenantId || isConnected) return;
    const interval = setInterval(() => {
      if (document.visibilityState !== "visible") return;
      if (isTerminalStatus(sessionRef.current?.status)) return;
      fetchEvents();
    }, 30_000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, sessionTenantId, isConnected]);

  return { events, setEvents, fetchEvents, scheduleFetchEvents, isStreamingMore };
}
