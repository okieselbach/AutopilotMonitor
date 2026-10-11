"use client";

import Link from "next/link";
import { route } from "@/lib/routes";
import { trackEvent } from "@/lib/appInsights";

/**
 * Shown on a failed session without a diagnostics package when the tenant has no usable upload
 * (sessionHintsLogic.showDiagnosticsSetupHint). Points to the settings instead of a one-click switch:
 * the destination is a deliberate choice (own storage or hosted, with its disclosure), and a finished
 * session has nothing to collect any more.
 */
export function DiagnosticsSetupHint({ canConfigure }: { canConfigure: boolean }) {
  return (
    <div className="mt-4 flex items-start gap-2 px-3 py-2 rounded-lg bg-sky-50 border border-sky-200 text-sm text-sky-800 dark:bg-sky-900/30 dark:border-sky-800 dark:text-sky-200">
      <svg className="w-4 h-4 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
      </svg>
      <span>
        <strong>No diagnostics package for this failed enrollment</strong>
        {" — diagnostics upload isn't set up. With Upload Mode “On Failure Only”, the next failed enrollment keeps its IME and agent logs. "}
        {canConfigure ? (
          <Link
            href={route("/settings/agent/diagnostics")}
            onClick={() => trackEvent("session_diag_hint_settings_clicked")}
            className="font-medium underline hover:no-underline"
          >
            Open diagnostics settings
          </Link>
        ) : (
          "A Tenant Admin can turn it on under Settings → Agent → Diagnostics Package."
        )}
      </span>
    </div>
  );
}
