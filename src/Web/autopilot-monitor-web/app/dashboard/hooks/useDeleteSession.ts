"use client";
import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { NotificationType } from "@/contexts/NotificationContext";
import { useSignalR } from "@/contexts/SignalRContext";
import {
  classifyDeleteResponse,
  classifyPollingResponse,
  dispatchSessionDeleted,
  type DeleteResponseAction,
} from "./deleteSessionResponse";
import { BULK_CONCURRENCY, runWithConcurrency, summarizeDeleteActions } from "./bulkActions";

export interface DeleteTarget {
  sessionId: string;
  tenantId: string;
  deviceName?: string;
}

export function useDeleteSession(
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: (type: NotificationType, title: string, message: string, key?: string, href?: string) => void,
  adminMode: boolean,
  onSessionDeleted: (sessionId: string) => void
) {
  // Sessions awaiting the user's confirmation. One entry for the row trash icon, many for the
  // table's Select mode — both go through the same confirm → DELETE → classify path.
  const [deleteTargets, setDeleteTargets] = useState<DeleteTarget[]>([]);
  const showDeleteConfirm = deleteTargets.length > 0;

  // V2 cascade path: sessions awaiting the worker's `sessionDeleted` SignalR push. Surfaced
  // to consumers so the dashboard table can render a per-row spinner (plan §5 PR5: "show
  // 'deletion queued' toast + spinner on the session row until SignalR notification arrives").
  const [pendingDeletions, setPendingDeletions] = useState<Set<string>>(new Set());
  // Reverse lookup so the SignalR handler can leave the per-session group it joined.
  const pendingTenantsRef = useRef<Map<string, string>>(new Map());

  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();

  const removePending = useCallback((sessionId: string) => {
    setPendingDeletions((prev) => {
      if (!prev.has(sessionId)) return prev;
      const next = new Set(prev);
      next.delete(sessionId);
      return next;
    });
    const tenantId = pendingTenantsRef.current.get(sessionId);
    pendingTenantsRef.current.delete(sessionId);
    if (tenantId && isConnected) {
      // Fire-and-forget leave; the SignalR layer no-ops if we're not in the group.
      leaveGroup(`session-${tenantId}-${sessionId}`).catch(() => { /* best-effort */ });
    }
  }, [isConnected, leaveGroup]);

  // Single SignalR subscription that dispatches by sessionId — one listener handles N
  // concurrent pending deletions without registering / unregistering per-session handlers.
  useEffect(() => {
    const handleSessionDeleted = (payload: unknown) => {
      const pendingIds = new Set(pendingTenantsRef.current.keys());
      const id = dispatchSessionDeleted(payload, pendingIds);
      if (!id) return;
      onSessionDeleted(id);
      removePending(id);
    };
    on("sessionDeleted", handleSessionDeleted);
    return () => off("sessionDeleted", handleSessionDeleted);
  }, [on, off, onSessionDeleted, removePending]);

  // Polling fallback for missed `sessionDeleted` events (plan §5 PR5 finding 3). SignalR
  // doesn't replay messages that fire while we're disconnected; the auto-reconnect rejoins
  // groups but does not back-fill events. Every 60s we re-fetch each pending session and
  // treat a 404 as "cascade completed". The interval is conservative (≤ 5 reqs/min/user with
  // a busy admin), well under the rate limit.
  useEffect(() => {
    if (pendingDeletions.size === 0) return;
    const intervalId = setInterval(async () => {
      for (const sessionId of Array.from(pendingTenantsRef.current.keys())) {
        const tenantId = pendingTenantsRef.current.get(sessionId);
        if (!tenantId) continue;
        try {
          const r = await authenticatedFetch(api.sessions.get(sessionId, tenantId), getAccessToken, { method: 'GET' });
          if (classifyPollingResponse(r.status) === 'deleted') {
            onSessionDeleted(sessionId);
            removePending(sessionId);
          }
          // 'wait' = row still there (cascade in progress or poisoned) — keep waiting.
          // Auth/rate-limit/5xx are also 'wait'; the next tick retries.
        } catch {
          // Network / auth blip — ignore, next tick retries. We never want to clear a
          // pending row on an inconclusive poll because the cascade may still be running.
        }
      }
    }, 60_000);
    return () => clearInterval(intervalId);
  }, [pendingDeletions, getAccessToken, onSessionDeleted, removePending]);

  /** Open the confirm dialog for one or more sessions. Empty input is a no-op. */
  const deleteSessions = (targets: DeleteTarget[]) => {
    if (targets.length === 0) return;
    setDeleteTargets(targets);
  };

  /**
   * Apply the state side-effects of one classified DELETE response. `notify` is off for
   * bulk runs, which emit a single summary toast instead of one per session.
   */
  const applyDeleteAction = (action: DeleteResponseAction, notify: boolean) => {
    switch (action.kind) {
      case 'queued':
        setPendingDeletions((prev) => {
          const next = new Set(prev);
          next.add(action.sessionId);
          return next;
        });
        pendingTenantsRef.current.set(action.sessionId, action.tenantId);
        if (isConnected) {
          joinGroup(`session-${action.tenantId}-${action.sessionId}`).catch(() => { /* best-effort */ });
        }
        trackEvent("session_deletion_queued", { inAdminMode: adminMode, manifestId: action.manifestId ?? "" });
        if (notify) {
          addNotification(
            'info',
            'Deletion queued',
            'The cascade worker is draining this session. The row will disappear when it completes.',
            `session-delete-queued-${action.sessionId}`,
          );
        }
        return;

      case 'conflict':
        if (notify) addNotification('warning', action.title, action.message, `session-delete-conflict-${action.sessionId}`);
        return;

      case 'unavailable':
        if (notify) {
          addNotification(
            'warning',
            'Deletion temporarily unavailable',
            action.message,
            `session-delete-unavailable-${action.sessionId}`,
          );
        }
        return;

      case 'notFound':
        // Already gone server-side — remove from the table to match reality.
        onSessionDeleted(action.sessionId);
        return;

      case 'error':
        if (notify) addNotification('error', 'Delete failed', action.message, `session-delete-error-${action.sessionId}`);
        return;
    }
  };

  const confirmDelete = async () => {
    if (deleteTargets.length === 0) return;
    const targets = deleteTargets;

    // Always close the confirm dialog before any async work — the user already committed.
    setDeleteTargets([]);

    if (targets.length === 1) {
      const { sessionId, tenantId } = targets[0];
      try {
        const response = await authenticatedFetch(api.sessions.delete(sessionId, tenantId), getAccessToken, {
          method: 'DELETE',
        });
        applyDeleteAction(await classifyDeleteResponse(response, sessionId, tenantId), true);
      } catch (error) {
        // Errors in the catch are network / auth failures, not HTTP-status branches.
        if (error instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        } else {
          console.error('Failed to delete session:', error);
          addNotification('error', 'Delete failed', 'Unable to reach the backend.', 'session-delete-network-error');
        }
      }
      return;
    }

    trackEvent("session_bulk_delete_confirmed", { inAdminMode: adminMode, count: targets.length });

    // Bulk: per-session network failures become 'error' actions so the run continues and the
    // summary reports them. An expired token aborts the remaining requests — retrying each
    // one would only produce the same failure N times.
    const abort: { tokenExpired: TokenExpiredError | null } = { tokenExpired: null };
    const actions = await runWithConcurrency(targets, BULK_CONCURRENCY, async ({ sessionId, tenantId }): Promise<DeleteResponseAction> => {
      if (abort.tokenExpired) return { kind: 'error', sessionId, message: abort.tokenExpired.message };
      try {
        const response = await authenticatedFetch(api.sessions.delete(sessionId, tenantId), getAccessToken, {
          method: 'DELETE',
        });
        return await classifyDeleteResponse(response, sessionId, tenantId);
      } catch (error) {
        if (error instanceof TokenExpiredError) abort.tokenExpired = error;
        else console.error('Failed to delete session:', error);
        return { kind: 'error', sessionId, message: error instanceof Error ? error.message : 'Unable to reach the backend.' };
      }
    });

    for (const action of actions) applyDeleteAction(action, false);

    if (abort.tokenExpired) {
      addNotification('error', 'Session Expired', abort.tokenExpired.message, 'session-expired-error');
    }
    const summary = summarizeDeleteActions(actions);
    addNotification(summary.type, summary.title, summary.message, 'session-bulk-delete-summary');
  };

  const cancelDelete = () => {
    setDeleteTargets([]);
  };

  return {
    showDeleteConfirm,
    deleteTargets,
    pendingDeletions,
    deleteSessions,
    confirmDelete,
    cancelDelete,
  };
}
