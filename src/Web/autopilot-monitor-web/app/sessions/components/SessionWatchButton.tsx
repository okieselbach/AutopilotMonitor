"use client";

import { useEffect, useState } from "react";
import { type AddNotification, notifyApiError } from "@/contexts/NotificationContext";
import { getSessionWatch, isPushChannelRequired, setSessionWatch } from "@/lib/pushPortalApi";

/** Terminal statuses: the session has ended, there is nothing left to be notified about. */
const TERMINAL_STATUSES = new Set(["Succeeded", "Failed", "Incomplete"]);

const TOAST_TITLE = "Notify me when done";
const TOAST_KEY = "session-watch";

interface SessionWatchButtonProps {
  sessionId: string;
  /** The session's tenant, passed as ?tenantId= like the annotations route (TenantScoping.QueryParam). */
  effectiveTenantId?: string;
  sessionStatus?: string;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: AddNotification;
}

/**
 * "Notify me when done" (plan push-relay): registers a watch that pushes to the caller's own
 * paired devices when this session reaches a terminal status. Hidden once the session is
 * terminal; the host gates it to Admins/Operators (and Global Admins) like Report Session.
 * The GET says whether watching is possible for this person at all (a Push channel in one of
 * their own scopes); without that the button stays hidden instead of letting a click fail.
 */
export default function SessionWatchButton({ sessionId, effectiveTenantId, sessionStatus, getAccessToken, addNotification }: SessionWatchButtonProps) {
  const [watching, setWatching] = useState<boolean | null>(null);
  const [available, setAvailable] = useState(true);
  const [busy, setBusy] = useState(false);

  const active = sessionId.length > 0 && !!sessionStatus && !TERMINAL_STATUSES.has(sessionStatus);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    const run = async () => {
      try {
        const result = await getSessionWatch(sessionId, effectiveTenantId, getAccessToken);
        if (cancelled) return;
        setWatching(result.watching);
        setAvailable(result.available !== false);
      } catch {
        // Best effort: an unknown state renders as "not watching"; the click reports the real error.
        if (!cancelled) setWatching(false);
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [active, sessionId, effectiveTenantId, getAccessToken]);

  if (!active || !available) return null;

  const toggle = async () => {
    if (busy || watching === null) return;
    setBusy(true);
    try {
      const next = !watching;
      const result = await setSessionWatch(sessionId, effectiveTenantId, next, getAccessToken);
      setWatching(result.watching);
      if (result.watching) {
        addNotification("success", TOAST_TITLE, "Your paired devices will be notified when this session ends.", TOAST_KEY);
      } else {
        addNotification("info", TOAST_TITLE, "You will not be notified for this session.", TOAST_KEY);
      }
    } catch (err) {
      if (isPushChannelRequired(err)) {
        addNotification("warning", TOAST_TITLE, "Enable a Push channel under Settings › Notifications first.", TOAST_KEY);
      } else {
        notifyApiError(addNotification, TOAST_TITLE, err, TOAST_KEY);
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <button
      type="button"
      onClick={() => void toggle()}
      disabled={busy || watching === null}
      aria-pressed={watching === true}
      title={watching ? "Stop notifying my devices when this session ends." : "Push to my paired devices when this session ends."}
      className={`px-4 py-2 rounded-md transition-colors flex items-center gap-2 text-sm disabled:opacity-50 disabled:cursor-not-allowed ${
        watching
          ? "bg-sky-100 border border-sky-300 text-sky-800 hover:bg-sky-200"
          : "bg-white border border-gray-200 text-gray-700 hover:bg-gray-50"
      }`}
    >
      <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
          d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
      </svg>
      {watching ? "Watching" : "Notify me when done"}
    </button>
  );
}
