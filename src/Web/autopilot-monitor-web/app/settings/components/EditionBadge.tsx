"use client";

import { useState } from "react";
import { editionLabel } from "@/lib/edition";
import { useTenantConfig } from "../TenantConfigContext";

/**
 * Edition badge + self-service trial CTA for the settings header.
 * - "Enterprise" / "Enterprise Trial — X days left" / "Community"
 * - "Start 30-day Enterprise trial" button only when the backend reports the trial as
 *   still available (never consumed + currently Community) AND the caller is a tenant admin.
 *   One-time action → guarded by an inline confirm step.
 */
export default function EditionBadge() {
  const { editionInfo, startTrial, startingTrial, user } = useTenantConfig();
  const [confirming, setConfirming] = useState(false);

  const isEnterprise = editionInfo.edition === "enterprise";
  const label = editionLabel(editionInfo);
  const canStartTrial =
    editionInfo.trialAvailable && (user?.isTenantAdmin === true || user?.isGlobalAdmin === true);

  return (
    <div className="flex items-center gap-3">
      <span
        className={`inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold ${
          isEnterprise
            ? "bg-purple-100 text-purple-800 border border-purple-300"
            : "bg-gray-100 text-gray-700 border border-gray-300"
        }`}
        title={
          isEnterprise
            ? editionInfo.isTrial
              ? "Enterprise trial is active — all Enterprise features are unlocked."
              : "This tenant is on the Enterprise edition."
            : "This tenant is on the Community edition."
        }
      >
        {label}
      </span>

      {canStartTrial && !confirming && (
        <button
          onClick={() => setConfirming(true)}
          className="text-xs font-medium text-purple-700 border border-purple-300 rounded-full px-3 py-1 hover:bg-purple-50 transition-colors"
        >
          Start 30-day Enterprise trial
        </button>
      )}

      {canStartTrial && confirming && (
        <span className="flex items-center gap-2 text-xs">
          <span className="text-gray-600">One-time trial — start now?</span>
          <button
            onClick={async () => {
              const ok = await startTrial();
              if (ok) setConfirming(false);
            }}
            disabled={startingTrial}
            className="font-medium text-white bg-purple-600 rounded-full px-3 py-1 hover:bg-purple-700 disabled:opacity-50 transition-colors"
          >
            {startingTrial ? "Starting…" : "Confirm"}
          </button>
          <button
            onClick={() => setConfirming(false)}
            disabled={startingTrial}
            className="text-gray-500 hover:text-gray-700"
          >
            Cancel
          </button>
        </span>
      )}
    </div>
  );
}
