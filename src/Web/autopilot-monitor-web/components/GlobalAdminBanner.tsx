"use client";

import type { ReactNode } from "react";

/** Minimal scope shape needed to derive the banner subtitle. */
export interface GlobalAdminBannerScope {
  isAggregatedGlobalView: boolean;
  isGlobalOverride: boolean;
  selectedTenantId: string;
  selectedTenantName?: string;
}

/**
 * Computes the dynamic "Global Admin View" subtitle for the aggregated-capable page variant:
 * aggregated → all-tenants text; override → "viewing tenant X"; otherwise → "access to all tenants".
 *
 * @param aggregatedText override the aggregated-mode wording (some pages say "aggregating data
 *   across all tenants" instead of the default).
 */
export function globalAdminSubtitle(
  scope: GlobalAdminBannerScope,
  aggregatedText = "aggregating across all tenants"
): string {
  if (scope.isAggregatedGlobalView) return aggregatedText;
  if (scope.isGlobalOverride) {
    return `viewing tenant ${scope.selectedTenantName ?? scope.selectedTenantId}`;
  }
  return "access to all tenants";
}

/**
 * The "Global Admin View" bar shown at the top of pages while a global admin browses with
 * Global-Admin mode enabled. Renders nothing when {@link show} is false.
 *
 * @param subtitle optional text after the dash. Defaults to the static "access to all tenants"
 *   (override-only pages); aggregated pages pass {@link globalAdminSubtitle} for dynamic text.
 */
export function GlobalAdminBanner({
  show,
  subtitle = "access to all tenants",
}: {
  show: boolean;
  subtitle?: ReactNode;
}) {
  if (!show) return null;

  return (
    <div className="bg-purple-700 text-white text-sm px-4 py-2 flex items-center justify-center space-x-2">
      <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
      </svg>
      <span className="font-medium">Global Admin View</span>
      <span className="text-purple-300">&mdash; {subtitle}</span>
    </div>
  );
}
