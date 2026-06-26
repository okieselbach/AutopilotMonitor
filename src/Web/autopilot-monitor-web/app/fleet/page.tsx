"use client";

import { useMemo } from "react";
import Link from "next/link";
import { useAuth } from "@/contexts/AuthContext";
import { useTenantList, type TenantInfo } from "@/hooks/useTenantList";
import { useFleetSummaries } from "./hooks/useFleetSummaries";
import { computeFleetRollup, type FleetSummary } from "./lib/fleetRollup";

const DAYS = 30;

/**
 * Fleet landing — the managed-tenant overview for a delegated ("MSP") admin.
 *
 * The tenant list comes from config/all (backend-bounded to the caller's subset, Phase 2b) and is
 * intersected with the JWT delegatedTenantIds as client-side defense in depth. Per-tenant health is fanned
 * out (bounded) over the Phase-2a single-tenant `/global/stats/sessions?tenantId=` endpoint — no all-tenants
 * code path. Cards are triage-ordered (most failures first). This is a pure stats overview; a card drills the
 * tenant into the dashboard (`/dashboard?tenant=<id>`), the cross-tenant bounded session browser.
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
  const tenantIds = useMemo(() => myTenants.map((t) => t.tenantId), [myTenants]);

  const { summaries, loading } = useFleetSummaries(tenantIds, DAYS);
  const rollup = useMemo(() => computeFleetRollup(Object.values(summaries)), [summaries]);

  // Triage order: worst first (most failures, then lowest success rate). Tenants still loading sort last;
  // within an equal bucket, by domain for stability.
  const ordered = useMemo(() => {
    return [...myTenants].sort((a, b) => {
      const sa = summaries[a.tenantId];
      const sb = summaries[b.tenantId];
      if (sa && sb) {
        if (sb.failedLastNDays !== sa.failedLastNDays) return sb.failedLastNDays - sa.failedLastNDays;
        if (sa.successRatePct !== sb.successRatePct) return sa.successRatePct - sb.successRatePct;
      } else if (sa && !sb) {
        return -1;
      } else if (!sa && sb) {
        return 1;
      }
      return (a.domainName || a.tenantId).localeCompare(b.domainName || b.tenantId);
    });
  }, [myTenants, summaries]);

  return (
    <div className="mx-auto max-w-7xl p-4 sm:p-6 lg:p-8">
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900 dark:text-white">Fleet</h1>
        <p className="mt-1 text-sm text-gray-500 dark:text-gray-400">
          {myTenants.length} managed tenant{myTenants.length === 1 ? "" : "s"} · last {DAYS} days
          {loading ? " · refreshing…" : ""}
        </p>
      </div>

      {/* Roll-up */}
      <div className="mb-6 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <RollupTile label="Tenants" value={String(myTenants.length)} />
        <RollupTile label="Active sessions" value={String(rollup.activeCount)} />
        <RollupTile label="Failed" value={String(rollup.failedLastNDays)} tone={rollup.failedLastNDays > 0 ? "danger" : "default"} />
        <RollupTile label="Success rate" value={`${rollup.successRatePct}%`} tone={successTone(rollup.successRatePct, rollup.succeededLastNDays + rollup.failedLastNDays)} />
      </div>

      {myTenants.length === 0 ? (
        <div className="rounded-lg border border-dashed border-gray-300 p-10 text-center text-gray-500 dark:border-gray-700 dark:text-gray-400">
          No managed tenants yet. Once tenants are delegated to you, they appear here.
        </div>
      ) : (
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {ordered.map((t) => (
            <FleetCard key={t.tenantId} tenant={t} summary={summaries[t.tenantId]} />
          ))}
        </div>
      )}
    </div>
  );
}

type Tone = "default" | "danger";

/**
 * Red emphasis only once there are TERMINAL sessions to judge — the success rate is terminal-only
 * (succeeded / succeeded+failed), so a tenant with only active/pending sessions reads 0% but isn't
 * "failing"; don't flag it.
 */
function successTone(pct: number, terminalCount: number): Tone {
  if (terminalCount === 0) return "default";
  return pct < 80 ? "danger" : "default";
}

function RollupTile({ label, value, tone = "default" }: { label: string; value: string; tone?: Tone }) {
  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4 dark:border-gray-700 dark:bg-gray-800">
      <div className="text-xs font-medium uppercase tracking-wider text-gray-400 dark:text-gray-500">{label}</div>
      <div className={`mt-1 text-2xl font-semibold ${tone === "danger" ? "text-red-600 dark:text-red-400" : "text-gray-900 dark:text-white"}`}>
        {value}
      </div>
    </div>
  );
}

function FleetCard({ tenant, summary }: { tenant: TenantInfo; summary?: FleetSummary }) {
  return (
    <Link
      href={`/dashboard?tenant=${encodeURIComponent(tenant.tenantId)}`}
      className="block rounded-lg border border-gray-200 bg-white p-5 shadow-sm transition-colors hover:border-blue-400 hover:shadow-md dark:border-gray-700 dark:bg-gray-800 dark:hover:border-blue-500"
    >
      <div className="truncate text-base font-semibold text-gray-900 dark:text-white">
        {tenant.domainName || "Unknown domain"}
      </div>
      <div className="mt-1 break-all font-mono text-xs text-gray-400 dark:text-gray-500">{tenant.tenantId}</div>

      <div className="mt-4 grid grid-cols-3 gap-2 text-center">
        <Metric label="Active" value={summary ? String(summary.activeCount) : "—"} />
        <Metric
          label="Failed"
          value={summary ? String(summary.failedLastNDays) : "—"}
          danger={!!summary && summary.failedLastNDays > 0}
        />
        <Metric
          label="Success"
          value={summary ? `${summary.successRatePct}%` : "—"}
          danger={!!summary && successTone(summary.successRatePct, summary.succeededLastNDays + summary.failedLastNDays) === "danger"}
        />
      </div>
    </Link>
  );
}

function Metric({ label, value, danger = false }: { label: string; value: string; danger?: boolean }) {
  return (
    <div>
      <div className={`text-lg font-semibold ${danger ? "text-red-600 dark:text-red-400" : "text-gray-900 dark:text-white"}`}>
        {value}
      </div>
      <div className="text-[11px] uppercase tracking-wider text-gray-400 dark:text-gray-500">{label}</div>
    </div>
  );
}
