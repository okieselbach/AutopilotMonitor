"use client";

import { useEffect, useRef } from "react";
import { Session } from "@/types";
import type { SignalRMessageName } from "@/lib/signalrMessages";
import type { JoinGroupOptions } from "@/contexts/SignalRContext";
import type { NotificationType } from "@/contexts/NotificationContext";

interface SignalRApi {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  on: (event: SignalRMessageName, handler: (...args: any[]) => void) => void;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  off: (event: SignalRMessageName, handler: (...args: any[]) => void) => void;
  isConnected: boolean;
  joinGroup: (group: string, options?: JoinGroupOptions) => Promise<void>;
  leaveGroup: (group: string) => Promise<void>;
}

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface UseProgressSignalRParams {
  session: Session | null;
  sessionRef: React.RefObject<Session | null>;
  signalR: SignalRApi;
  scheduleFetchEvents: (delayMs?: number) => void;
  addNotification: AddNotification;
}

/**
 * Owns the progress page's SignalR integration:
 *  - joins the session-specific group when a session is selected, presenting the device's
 *    serial number as knowledge proof (roleless progress viewers are refused without it;
 *    the tenant-wide broadcast group stays member-role gated server-side, and all signals
 *    for the selected session — eventStream on ingest, newevents on admin
 *    mark-succeeded/failed — arrive on the session group anyway)
 *  - surfaces a refused join as a notification instead of a silently frozen page
 *    (a swallowed join 403 is exactly how the c4dabeee regression stayed invisible)
 *  - listens for newevents / newSession / eventStream → debounced refetch
 *  - cleans up groups + handlers on unmount / session change
 */
export function useProgressSignalR({
  session,
  sessionRef,
  signalR,
  scheduleFetchEvents,
  addNotification,
}: UseProgressSignalRParams): void {
  const { on, off, isConnected, joinGroup, leaveGroup } = signalR;

  // Primitives extracted so the join effect keys on identity of the selected
  // session, not object identity — a refetched session object must not rejoin.
  const sessionId = session?.sessionId;
  const sessionTenantId = session?.tenantId;
  const sessionSerial = session?.serialNumber;

  // Ref-stabilized so an unstable addNotification identity can never churn the join effect
  // (which would leave + rejoin the group on every render).
  const addNotificationRef = useRef(addNotification);
  useEffect(() => {
    addNotificationRef.current = addNotification;
  }, [addNotification]);

  useEffect(() => {
    if (!isConnected || !sessionId || !sessionTenantId) return;

    const sessionGroup = `session-${sessionTenantId}-${sessionId}`;
    console.log("[Progress] Joining session group:", sessionGroup);
    joinGroup(sessionGroup, {
      serialNumber: sessionSerial,
      onDenied: (status) => {
        console.warn(`[Progress] Session group join denied (status ${status})`);
        addNotificationRef.current(
          "warning",
          "Live Updates Unavailable",
          "Real-time progress updates could not be enabled. The page will still refresh when you search again.",
          "progress-join-denied",
        );
      },
    });

    return () => {
      console.log("[Progress] Leaving session group:", sessionGroup);
      leaveGroup(sessionGroup);
    };
  }, [isConnected, sessionId, sessionTenantId, sessionSerial, joinGroup, leaveGroup]);

  useEffect(() => {
    const scheduleRefetch = (source: string, sessionId: string) => {
      if (!sessionRef.current || sessionId !== sessionRef.current.sessionId) return;
      console.log(`[Progress] ${source} signal for current session, scheduling refetch`);
      scheduleFetchEvents(500);
    };

    const handleNewEvents = (data: { sessionId: string }) => {
      scheduleRefetch("newevents", data.sessionId);
    };
    const handleEventStream = (data: { sessionId: string }) => {
      scheduleRefetch("eventStream", data.sessionId);
    };

    on("newevents", handleNewEvents);
    on("newSession", handleNewEvents);
    on("eventStream", handleEventStream);
    return () => {
      off("newevents", handleNewEvents);
      off("newSession", handleNewEvents);
      off("eventStream", handleEventStream);
    };
  }, [on, off, sessionRef, scheduleFetchEvents]);
}
