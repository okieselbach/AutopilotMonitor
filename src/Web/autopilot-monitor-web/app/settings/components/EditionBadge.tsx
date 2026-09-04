"use client";

import Link from "next/link";
import { editionLabel, isProViaMsp } from "@/lib/edition";
import { useTenantConfig } from "../TenantConfigContext";

/**
 * Edition badge for the settings header.
 * - "Pro" / "Pro (MSP)" / "Pro Trial — X days left" / "Community"
 * - For Community tenants, an "Upgrade plan" link to the Plan section, where the full picture
 *   (what Pro adds) and the trial CTA live. The badge itself only reports status.
 */
export default function EditionBadge() {
  const { editionInfo } = useTenantConfig();

  const isPro = editionInfo.edition === "pro";
  const label = editionLabel(editionInfo);

  return (
    <div className="flex items-center gap-3">
      <span
        className={`inline-flex items-center px-2.5 py-1 rounded-full text-xs font-semibold ${
          isPro
            ? "bg-purple-100 text-purple-800 border border-purple-300"
            : "bg-gray-100 text-gray-700 border border-gray-300"
        }`}
        title={
          isPro
            ? isProViaMsp(editionInfo)
              ? "Pro is included through the organization that manages this tenant. It ends with that delegation."
              : editionInfo.isTrial
                ? "Pro trial is active — all Pro features are unlocked."
                : "This tenant is on the Pro plan."
            : "This tenant is on the Community plan."
        }
      >
        {label}
      </span>

      {!isPro && (
        <Link
          href="/settings/tenant/plan"
          className="inline-flex items-center gap-1 text-xs font-medium text-purple-700 border border-purple-300 rounded-full px-3 py-1 hover:bg-purple-50 transition-colors"
        >
          <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 10l7-7m0 0l7 7m-7-7v18" />
          </svg>
          Upgrade plan
        </Link>
      )}
    </div>
  );
}
