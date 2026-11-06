"use client";

import { useState, useSyncExternalStore } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { exitViewAs, readViewAs, takeViewAsNotice } from "@/lib/viewAs";

const noSubscription = () => () => {};
// A refused view-as ends with a reload (lib/viewAs.ts); its reason is handed out once per page load.
const refusedNotice = typeof window === "undefined" ? null : takeViewAsNotice();
const getRefusedNotice = () => refusedNotice;
const noValue = () => null;

const BAR_CLASS = "bg-purple-700 text-white text-sm px-4 py-2 flex flex-wrap items-center justify-center gap-x-2 gap-y-1";

/**
 * The one deliberate difference of a view-as tab from the customer's own portal: a slim bar that names the
 * viewed tenant and the emulated role, says read-only, and leaves. It sits in the root layout, outside
 * ProtectedRoute, so Exit stays reachable on the suspended, activation and Terms + DPA pages too. On an
 * ordinary tab it shows once why a view-as was refused.
 */
export function ViewAsBar() {
  const selection = useSyncExternalStore(noSubscription, readViewAs, noValue);
  const notice = useSyncExternalStore(noSubscription, getRefusedNotice, noValue);
  const [noticeDismissed, setNoticeDismissed] = useState(false);
  const { user } = useAuth();

  if (selection) {
    // The backend's name for the tenant once auth/me answered; the id until then (or when it refused).
    const tenantName = user?.viewAs?.tenantName ?? selection.tenantId;
    return (
      <div className={BAR_CLASS} role="status">
        <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
        </svg>
        <span className="font-medium">Viewing as {tenantName}</span>
        <span className="text-purple-300">&mdash; {selection.role}, read-only</span>
        <button
          type="button"
          onClick={exitViewAs}
          className="ml-2 rounded border border-purple-300 px-2 py-0.5 text-xs font-medium hover:bg-purple-600 focus:outline-none focus-visible:ring-2 focus-visible:ring-white"
        >
          Exit
        </button>
      </div>
    );
  }

  if (notice && !noticeDismissed) {
    return (
      <div className={BAR_CLASS} role="alert">
        <span className="font-medium">View as ended</span>
        <span className="text-purple-300">&mdash; {notice}</span>
        <button
          type="button"
          onClick={() => setNoticeDismissed(true)}
          aria-label="Dismiss"
          className="ml-2 rounded px-1.5 text-purple-200 hover:text-white focus:outline-none focus-visible:ring-2 focus-visible:ring-white"
        >
          &times;
        </button>
      </div>
    );
  }

  return null;
}
