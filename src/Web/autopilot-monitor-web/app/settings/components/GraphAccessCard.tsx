"use client";

import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/lib/config";
import { GRAPH_ACCESS_TITLE, graphAccessCause } from "@/lib/graphAccess";

interface GraphAccessCardProps {
  /** The refusal says the enterprise application is gone (feature-flags `graphAccessAppMissing`). */
  appMissing: boolean;
  /** When Autopilot Monitor first noticed the refusal (tenant config `graphAccessRejectedSince`). */
  since?: string | null;
  /** The Entra error of the refusal (tenant config `graphAccessRejectedCode`). */
  code?: string | null;
  busy: boolean;
  onRestore: () => void;
  onDetectExistingAccess: () => void;
}

/**
 * Microsoft Entra keeps refusing the tenant's app token, so its agents get no validation. Takes the
 * place of the migration nudge: for a tenant still on the previous app, the same consent also
 * switches it over. Both actions repair the access without touching a validation gate.
 */
export function GraphAccessCard({ appMissing, since, code, busy, onRestore, onDetectExistingAccess }: GraphAccessCardProps) {
  return (
    <div className="bg-red-50 border-2 border-red-300 rounded-lg p-5 shadow-sm dark:bg-red-950/40 dark:border-red-700/60">
      <div className="flex items-start gap-3">
        <svg className="w-6 h-6 text-red-600 dark:text-red-400 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
        </svg>
        <div className="min-w-0 flex-1 text-sm text-red-900 dark:text-red-100">
          <p className="font-semibold text-base">{GRAPH_ACCESS_TITLE}</p>
          <p className="mt-1.5 text-red-800 dark:text-red-200">{graphAccessCause(appMissing)}</p>
          <p className="mt-1.5 text-red-800 dark:text-red-200">
            Restoring access takes admin consent from a <strong>Global Administrator</strong> or{" "}
            <strong>Privileged Role Administrator</strong>.
          </p>
          {(since || code) && (
            <p className="mt-1.5 text-xs text-red-700 dark:text-red-300">
              {since && <>Since {new Date(since).toLocaleString()}</>}
              {since && code && " · "}
              {code && <code>{code}</code>}
            </p>
          )}
          <a
            href={`${DOCS_URL}${DOCS_PATHS.enrollmentMonitoringInterrupted}`}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 mt-2.5 text-sm font-medium text-red-700 hover:text-red-900 underline dark:text-red-300 dark:hover:text-red-100"
          >
            What this means and how to fix it
            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
            </svg>
          </a>
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={onRestore}
              disabled={busy}
              className="px-3 py-1.5 text-sm font-medium text-white bg-red-600 rounded-lg hover:bg-red-700 disabled:opacity-60 disabled:cursor-not-allowed transition-colors"
            >
              Restore access
            </button>
            <button
              type="button"
              onClick={onDetectExistingAccess}
              disabled={busy}
              className="text-sm font-medium text-red-700 hover:text-red-900 underline underline-offset-2 disabled:opacity-60 disabled:cursor-not-allowed dark:text-red-300 dark:hover:text-red-100"
            >
              Detect existing access
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
