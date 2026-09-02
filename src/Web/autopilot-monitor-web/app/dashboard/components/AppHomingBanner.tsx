"use client";

import { useEffect, useSyncExternalStore } from "react";
import Link from "next/link";
import { DOCS_URL } from "@/utils/config";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { trackEvent } from "@/lib/appInsights";

// Session-scoped dismissal (same shape as ActivelyDevelopedBanner): hidden across reloads in
// this tab, back in every new tab — the switch is a one-time minute of admin work, so the
// nudge stays persistent but never blocks anything. Module-level store because a same-tab
// sessionStorage write fires no "storage" event.
const DismissKey = "appHomingBannerDismissed";
const listeners = new Set<() => void>();

const getSnapshot = (): boolean => sessionStorage.getItem(DismissKey) === "1";
const getServerSnapshot = (): boolean => false;

function subscribe(onStoreChange: () => void): () => void {
  listeners.add(onStoreChange);
  return () => listeners.delete(onStoreChange);
}

function dismiss(): void {
  sessionStorage.setItem(DismissKey, "1");
  for (const listener of [...listeners]) listener();
}

/**
 * Dashboard nudge of the dual app-reg migration, shown to tenant admins while the tenant still
 * runs on the previous app registration and the self-service switch is open
 * (feature-flags `appHomingFunnelActive`). Keyed on the TENANT's state, not on which app this
 * browser signed in with: the fix is a tenant-wide admin consent. The consent flow itself
 * lives in Settings → Autopilot Validation (its redirect returns there) — this banner only
 * links to it and to the docs.
 */
export function AppHomingBanner() {
  const dismissed = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);

  useEffect(() => {
    if (!dismissed) trackEvent("app_homing_banner_shown");
  }, [dismissed]);

  if (dismissed) return null;

  return (
    <div className="mb-6 bg-blue-50 border border-blue-300 rounded-lg px-4 py-3 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 dark:bg-blue-950/30 dark:border-blue-700/50">
      <div className="flex items-start gap-3">
        <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
        </svg>
        <p className="text-sm text-blue-800 dark:text-blue-300">
          <span className="font-semibold">Please switch to the new Autopilot Monitor app registration.</span>{" "}
          Your organization still signs in through the previous app. A one-time admin consent
          (about a minute) switches it over — until then, colleagues signing in from a new
          browser or device see an extra consent prompt.{" "}
          <a
            href={`${DOCS_URL}${DOCS_PATHS.appRegistrationMigration}`}
            target="_blank"
            rel="noopener noreferrer"
            className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
            onClick={() => trackEvent("app_homing_banner_clicked", { link: "docs" })}
          >
            Read why and how
          </a>
          .
        </p>
      </div>
      <div className="flex items-center gap-2 shrink-0">
        <Link
          href="/settings/tenant/autopilot"
          className="inline-flex items-center gap-2 bg-blue-600 text-white font-medium text-sm px-3 py-1.5 rounded-lg hover:bg-blue-700 transition-colors"
          onClick={() => trackEvent("app_homing_banner_clicked", { link: "settings" })}
        >
          Switch in Settings
        </Link>
        <button
          type="button"
          onClick={() => {
            trackEvent("app_homing_banner_dismissed");
            dismiss();
          }}
          aria-label="Hide for this browser tab"
          title="Hide for this browser tab"
          className="p-1 rounded text-blue-300 hover:text-blue-600 hover:bg-blue-100 transition-colors dark:text-blue-700 dark:hover:text-blue-300 dark:hover:bg-blue-900/40"
        >
          <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      </div>
    </div>
  );
}
