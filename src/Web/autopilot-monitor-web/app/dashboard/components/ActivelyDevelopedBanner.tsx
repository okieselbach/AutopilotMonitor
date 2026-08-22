"use client";

import { useSyncExternalStore } from "react";
import { DOCS_URL } from "@/utils/config";
import { trackEvent } from "@/lib/appInsights";

// Session-scoped dismissal: sessionStorage is per-tab, so the banner stays hidden
// across reloads in this tab (service-desk monitor use case) but reappears in every
// new tab — deliberately never a permanent opt-out. Module-level store in the
// themeStore shape: a same-tab sessionStorage write fires no "storage" event, and
// useSyncExternalStore keeps the statically prerendered markup hydration-safe.
const DismissKey = "devBannerDismissed";
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

const linkClassName =
  "underline font-medium hover:text-green-600 dark:hover:text-blue-200";

export function ActivelyDevelopedBanner() {
  const dismissed = useSyncExternalStore(subscribe, getSnapshot, getServerSnapshot);
  if (dismissed) return null;

  const trackLink = (link: string) => trackEvent("dev_banner_link_clicked", { link });

  return (
    <div className="mb-4 bg-blue-50 border border-blue-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-blue-950/30 dark:border-blue-700/50">
      <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
      </svg>
      <p className="text-sm text-blue-800 dark:text-blue-300">
        <span className="font-semibold">Actively developed.</span>{" "}
        Autopilot Monitor recognizes a wide range of deployment scenarios and improves
        continuously — your reports directly shape it.{" "}
        If something looks off, check the{" "}
        <a
          href={`${DOCS_URL}/changelog/platform-changelog`}
          target="_blank"
          rel="noopener noreferrer"
          className={linkClassName}
          onClick={() => trackLink("platform_changelog")}
        >
          Platform Changelog
        </a>{" "}
        or{" "}
        <a
          href={`${DOCS_URL}/troubleshooting/service-announcements`}
          target="_blank"
          rel="noopener noreferrer"
          className={linkClassName}
          onClick={() => trackLink("service_announcements")}
        >
          Service Announcements
        </a>
        .{" "}
        Feedback or bug report?{" "}
        <a
          href="https://github.com/okieselbach/AutopilotMonitor/issues"
          target="_blank"
          rel="noopener noreferrer"
          className={linkClassName}
          onClick={() => trackLink("github_issues")}
        >
          Open a GitHub issue
        </a>
        {" "}or message me on{" "}
        <a
          href="https://www.linkedin.com/in/oliver-kieselbach/"
          target="_blank"
          rel="noopener noreferrer"
          className={linkClassName}
          onClick={() => trackLink("linkedin")}
        >
          LinkedIn
        </a>
        .
      </p>
      <button
        type="button"
        onClick={() => {
          trackEvent("dev_banner_dismissed");
          dismiss();
        }}
        aria-label="Hide for this browser tab"
        title="Hide for this browser tab"
        className="ml-auto shrink-0 -mr-1 -mt-1 p-1 rounded text-blue-300 hover:text-blue-600 hover:bg-blue-100 transition-colors dark:text-blue-700 dark:hover:text-blue-300 dark:hover:bg-blue-900/40"
      >
        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
        </svg>
      </button>
    </div>
  );
}
