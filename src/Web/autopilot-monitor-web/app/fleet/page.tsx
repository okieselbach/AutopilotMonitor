"use client";

import { useMemo } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { useTenantList } from "@/hooks/useTenantList";

/**
 * Fleet landing — the managed-tenant overview for a delegated ("MSP") admin.
 *
 * The tenant list comes from config/all, which the backend bounds to the caller's managed tenants
 * (GlobalReadOrDelegatedSubset tier, Phase 2b). We additionally intersect with the JWT-surfaced
 * delegatedTenantIds as client-side defense in depth, so the grid can never render a tenant outside scope
 * even if a future endpoint change widened the response.
 *
 * Phase 3b renders the read-only overview grid. Per-tenant health metrics (3c) and drill-in into a managed
 * tenant's dashboards (3d) build on this.
 */
export default function FleetPage() {
  const { user } = useAuth();
  const isDelegated = user?.isDelegated ?? false;

  const allowed = useMemo(
    () => new Set((user?.delegatedTenantIds ?? []).map((t) => t.toLowerCase())),
    [user?.delegatedTenantIds]
  );
  const tenants = useTenantList(isDelegated);
  const myTenants = useMemo(
    () => tenants.filter((t) => allowed.has(t.tenantId.toLowerCase())),
    [tenants, allowed]
  );

  return (
    <div className="mx-auto max-w-7xl p-4 sm:p-6 lg:p-8">
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900 dark:text-white">Fleet</h1>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
          {myTenants.length} managed tenant{myTenants.length === 1 ? "" : "s"} · read-only monitoring
        </p>
      </div>

      {myTenants.length === 0 ? (
        <div className="rounded-lg border border-dashed border-gray-300 p-10 text-center text-gray-500 dark:border-gray-700 dark:text-gray-400">
          No managed tenants yet. Once tenants are delegated to you, they appear here.
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {myTenants.map((t) => (
            <div
              key={t.tenantId}
              className="rounded-lg border border-gray-200 bg-white p-5 shadow-sm dark:border-gray-700 dark:bg-gray-800"
            >
              <div className="truncate text-base font-semibold text-gray-900 dark:text-white">
                {t.domainName || "Unknown domain"}
              </div>
              <div className="mt-1 break-all font-mono text-xs text-gray-400 dark:text-gray-500">
                {t.tenantId}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
