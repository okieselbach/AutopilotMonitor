"use client";

import { useCallback, useState } from "react";
import { trackEvent } from "@/lib/appInsights";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { DOCS_URL } from "@/utils/config";

interface AppHomingAddOnStepProps {
  /** Optional Graph add-on roles the previous app holds but the new app still lacks. */
  missingRoles: readonly string[];
  /** Grant command pre-filled with the NEW app's client id and exactly these roles. */
  command: string;
  busy: boolean;
  onDetectExistingAccess: () => void;
}

/**
 * Self-service funnel, second stop: the admin consent for the new app succeeded, but the tenant
 * also granted the previous app optional add-on permissions (Settings → Optional Graph
 * capabilities). The switch waits until the new app holds them too — otherwise the feature they
 * power would silently stop working. No re-consent can add them; only the grant script against
 * the new app can, so this step hands the admin that exact command and the re-check button.
 * Same blue family as the funnel banner: it is progress, never an error.
 */
export function AppHomingAddOnStep({ missingRoles, command, busy, onDetectExistingAccess }: AppHomingAddOnStepProps) {
  const [copied, setCopied] = useState(false);

  const copy = useCallback(async () => {
    try {
      await navigator.clipboard.writeText(command);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
      // High-intent signal: the admin is about to run the grant against the new app.
      trackEvent("app_homing_addon_command_copied", { roleCount: missingRoles.length });
    } catch {
      // Older browsers / iframes — best effort only.
    }
  }, [command, missingRoles.length]);

  return (
    <div className="bg-gradient-to-r from-blue-50 to-indigo-50 border-2 border-blue-300 rounded-lg p-5 shadow-sm dark:from-blue-950/40 dark:to-indigo-950/40 dark:border-blue-700/60">
      <div className="flex items-start gap-3">
        <svg className="w-6 h-6 text-blue-600 dark:text-blue-400 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4" />
        </svg>
        <div className="min-w-0 flex-1 text-sm text-blue-900 dark:text-blue-100">
          <p className="font-semibold text-base">
            One more step: grant your optional Graph permissions to the new app
          </p>
          <p className="mt-1.5 text-blue-800 dark:text-blue-200">
            Your admin consent for the new app succeeded. Your tenant also granted the{" "}
            <strong>previous</strong> app optional Graph add-on permissions that the new app does
            not hold yet. So that nothing stops working, the switch waits until the new app holds
            them too:
          </p>
          <ul className="mt-1.5 list-disc list-inside font-mono text-xs text-blue-900 dark:text-blue-100">
            {missingRoles.map((role) => (
              <li key={role}>{role}</li>
            ))}
          </ul>
          <p className="mt-1.5 text-blue-800 dark:text-blue-200">
            Run the command below once with an account that can assign application permissions
            (<strong>Global Administrator</strong>, <strong>Privileged Role Administrator</strong> or{" "}
            <strong>Cloud Application Administrator</strong>) — Azure Cloud Shell is the easiest
            place. Then click <strong>Detect existing access</strong>: the switch completes
            automatically.
          </p>
          <pre className="mt-2.5 bg-gray-900 text-gray-100 text-xs font-mono p-3 rounded overflow-x-auto">
{command}
          </pre>
          <div className="mt-3 flex flex-wrap items-center gap-3">
            <button
              type="button"
              onClick={() => { void copy(); }}
              className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 transition-colors"
            >
              {copied ? "Copied!" : "Copy command"}
            </button>
            <button
              type="button"
              onClick={onDetectExistingAccess}
              disabled={busy}
              className="text-sm font-medium text-blue-700 hover:text-blue-900 underline underline-offset-2 disabled:opacity-60 disabled:cursor-not-allowed dark:text-blue-300 dark:hover:text-blue-100"
            >
              {busy ? "Checking…" : "Detect existing access"}
            </button>
          </div>
          <p className="mt-2.5 text-xs text-blue-700 dark:text-blue-300">
            <a
              href={`${DOCS_URL}${DOCS_PATHS.appRegistrationMigrationAddOns}`}
              target="_blank"
              rel="noopener noreferrer"
              className="underline hover:text-blue-900 dark:hover:text-blue-100"
            >
              Why this step is needed
            </a>
            {" · "}
            <a
              href={`${DOCS_URL}${DOCS_PATHS.optionalGraphPermissions}`}
              target="_blank"
              rel="noopener noreferrer"
              className="underline hover:text-blue-900 dark:hover:text-blue-100"
            >
              What the grant script does
            </a>
          </p>
        </div>
      </div>
    </div>
  );
}
