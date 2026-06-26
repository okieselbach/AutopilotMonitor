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
 * The cross-tenant view bar shown at the top of pages while a global admin (purple "Global Admin View")
 * or a delegated ("MSP") admin (blue "Delegated Admin View") browses cross-tenant. Renders nothing when
 * {@link show} is false.
 *
 * @param subtitle optional text after the dash. Defaults to the static "access to all tenants"
 *   (override-only pages); aggregated pages pass {@link globalAdminSubtitle} for dynamic text.
 * @param delegated when true, render the delegated ("MSP") variant — blue, "Delegated Admin View" — to
 *   make clear the cross-tenant scope is bounded to the caller's managed subset (not the whole platform).
 */
export function GlobalAdminBanner({
  show,
  subtitle = "access to all tenants",
  delegated = false,
}: {
  show: boolean;
  subtitle?: ReactNode;
  delegated?: boolean;
}) {
  if (!show) return null;

  const barClass = delegated
    ? "bg-blue-700 text-white text-sm px-4 py-2 flex items-center justify-center space-x-2"
    : "bg-purple-700 text-white text-sm px-4 py-2 flex items-center justify-center space-x-2";
  const subtitleClass = delegated ? "text-blue-200" : "text-purple-300";

  return (
    <div className={barClass}>
      <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
      </svg>
      <span className="font-medium">{delegated ? "Delegated Admin View" : "Global Admin View"}</span>
      <span className={subtitleClass}>&mdash; {subtitle}</span>
    </div>
  );
}
