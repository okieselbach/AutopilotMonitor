"use client";

import { useEffect } from "react";
import Link from "next/link";
import { DOCS_URL } from "@/lib/config";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { trackEvent } from "@/lib/appInsights";
import { GRAPH_ACCESS_TITLE, graphAccessCause, graphAccessNextStep } from "@/lib/graphAccess";

interface GraphAccessBannerProps {
  /** The refusal says the enterprise application is gone (feature-flags `graphAccessAppMissing`). */
  appMissing: boolean;
  /** Tenant admins and Global Admins only: a delegated admin's link would target their own tenant. */
  canRestore: boolean;
}

/**
 * Microsoft Entra has kept refusing the tenant's app token, so its agents get no validation
 * (feature-flags `graphAccessRejected`, set only after a refusal lasted 15 minutes). Not
 * dismissable: it goes away with the state. Everything a reader might still ask, such as how long
 * it stays after a repair, belongs in the linked article.
 */
export function GraphAccessBanner({ appMissing, canRestore }: GraphAccessBannerProps) {
  useEffect(() => {
    trackEvent("graph_access_banner_shown", { appMissing });
  }, [appMissing]);

  return (
    <div className="mb-6 bg-red-600 border-2 border-red-700 rounded-xl p-5 shadow-lg">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="flex items-start gap-3">
          <svg className="w-6 h-6 text-white mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
          </svg>
          <div>
            <p className="text-base font-bold text-white">{GRAPH_ACCESS_TITLE}</p>
            <p className="text-sm text-red-100 mt-0.5">
              {graphAccessCause(appMissing)} {graphAccessNextStep(appMissing)}{" "}
              <a
                href={`${DOCS_URL}${DOCS_PATHS.enrollmentMonitoringInterrupted}`}
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium text-white hover:text-red-100"
                onClick={() => trackEvent("graph_access_banner_clicked", { link: "docs" })}
              >
                What this means and how to fix it
              </a>
            </p>
          </div>
        </div>
        {canRestore && (
          <Link
            href="/settings/tenant/autopilot"
            className="shrink-0 inline-flex items-center justify-center gap-2 bg-white text-red-700 font-semibold text-sm px-4 py-2 rounded-lg hover:bg-red-50 transition-colors"
            onClick={() => trackEvent("graph_access_banner_clicked", { link: "settings" })}
          >
            Restore access
          </Link>
        )}
      </div>
    </div>
  );
}
