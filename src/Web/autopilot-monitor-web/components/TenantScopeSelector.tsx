"use client";

import type { TenantInfo } from "@/hooks/useTenantList";

/** Minimal scope shape the selector needs — satisfied by both GlobalAdminScope and AggregatedAdminScope. */
export interface TenantSelectorScope {
  isGlobalAdmin: boolean;
  tenants: TenantInfo[];
  selectedTenantId: string;
  setSelectedTenantId: (id: string) => void;
  /** True for a delegated ("MSP") caller — suppresses the "All tenants" aggregate option (never served to delegated). */
  isDelegatedScope?: boolean;
}

/**
 * Header dropdown that lets a global admin point a page at any tenant.
 * Renders nothing unless the user is a global admin with a non-empty tenant list, so callers
 * can drop it into a header unconditionally.
 *
 * @param allowAggregated when true, prepends an "All tenants (aggregated)" option (value "")
 *   for the aggregated-capable page variant, plus a one-click "×" reset back to the aggregate
 *   while a specific tenant is selected. Defaults to false (override-only pages).
 * @param themed when true, adds dark-mode (`dark:`) variants so the control matches dark-aware
 *   headers (e.g. the SLA page). Defaults to false — most consuming pages are light-only and a
 *   dark selector on their non-themed header would itself be a mismatch.
 */
export function TenantScopeSelector({
  scope,
  label = "Tenant:",
  allowAggregated = false,
  themed = false,
}: {
  scope: TenantSelectorScope;
  label?: string;
  allowAggregated?: boolean;
  themed?: boolean;
}) {
  if (!scope.isGlobalAdmin || scope.tenants.length === 0) return null;

  const labelClass = themed
    ? "text-sm text-gray-500 dark:text-gray-400 hidden sm:inline"
    : "text-sm text-gray-500 hidden sm:inline";
  const selectClass = themed
    ? "text-sm border border-gray-300 dark:border-gray-600 rounded-md px-2 py-1.5 max-w-[220px] sm:max-w-xs bg-white dark:bg-gray-700 text-gray-900 dark:text-white"
    : "text-sm border border-gray-300 rounded-md px-2 py-1.5 max-w-[220px] sm:max-w-xs";
  const resetClass = themed
    ? "text-gray-400 hover:text-gray-600 dark:text-gray-500 dark:hover:text-gray-300 transition-colors"
    : "text-gray-400 hover:text-gray-600 transition-colors";

  const showReset = allowAggregated && !scope.isDelegatedScope && scope.selectedTenantId !== "";

  return (
    <div className="flex items-center gap-3">
      <label className={labelClass}>{label}</label>
      <select
        value={scope.selectedTenantId}
        onChange={(e) => scope.setSelectedTenantId(e.target.value)}
        className={selectClass}
      >
        {allowAggregated && !scope.isDelegatedScope && <option value="">All tenants (aggregated)</option>}
        {scope.tenants.map((t) => (
          <option key={t.tenantId} value={t.tenantId}>
            {t.domainName
              ? `${t.domainName} (${t.tenantId.substring(0, 8)}…)`
              : t.tenantId}
            {t.isHome ? " — your tenant" : ""}
          </option>
        ))}
      </select>
      {showReset && (
        <button
          type="button"
          onClick={() => scope.setSelectedTenantId("")}
          className={resetClass}
          title="Back to all tenants (aggregated)"
          aria-label="Back to all tenants (aggregated)"
        >
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
          </svg>
        </button>
      )}
    </div>
  );
}
