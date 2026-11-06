"use client";

import { useMemo, useState } from "react";
import Link from "next/link";
import type { TenantActivityTenantItem } from "@/lib/generated/wire-types.generated";
import { tenantManagementUrl } from "@/lib/routes";
import { planEditionOf } from "../../../components/tenantFilters";
import { Chevron, EditionBadge } from "../listParts";
import { lifecycleFigures, lifecycleGroups, type LifecycleStage } from "./tenantActivityLogic";

const STAGE_TEXT: Record<LifecycleStage, { title: string; hint: string }> = {
  new: { title: "New", hint: "first sessions in this window" },
  declining: { title: "Declining", hint: "at most half the enrollments of the window before, which had 5 or more" },
  dormant: { title: "Dormant", hint: "enrolled before, no activity in the last 30 days" },
  neverEnrolled: { title: "Never enrolled", hint: "signed up, no enrollment on record" },
};

// The long lists start closed; a click opens them.
const INITIALLY_CLOSED: readonly LifecycleStage[] = ["dormant", "neverEnrolled"];

/** The tenants that need a look, one group per lifecycle stage, one line per tenant. */
export function LifecycleTab({
  tenants,
  portalAvailable,
  nowMs,
}: {
  tenants: readonly TenantActivityTenantItem[];
  portalAvailable: boolean;
  nowMs: number;
}) {
  const groups = useMemo(() => lifecycleGroups(tenants), [tenants]);
  const [closed, setClosed] = useState<ReadonlySet<LifecycleStage>>(() => new Set(INITIALLY_CLOSED));
  const toggle = (stage: LifecycleStage) =>
    setClosed((current) => {
      const next = new Set(current);
      if (!next.delete(stage)) next.add(stage);
      return next;
    });

  return (
    <div className="space-y-4">
      {groups.map(({ stage, rows }) => {
        const isOpen = !closed.has(stage);
        return (
          <section key={stage} aria-label={STAGE_TEXT[stage].title} className="overflow-hidden rounded-lg bg-white shadow">
            <button
              type="button"
              onClick={() => toggle(stage)}
              aria-expanded={isOpen}
              className="flex w-full items-center gap-2 px-4 py-3 text-left hover:bg-gray-50 sm:px-6"
            >
              <Chevron open={isOpen} />
              <span className="font-medium text-gray-900">{STAGE_TEXT[stage].title}</span>
              <span className="rounded-full bg-gray-100 px-2 py-0.5 text-xs font-medium tabular-nums text-gray-700">
                {rows.length.toLocaleString()}
              </span>
              <span className="ml-auto hidden truncate text-xs text-gray-500 sm:inline">{STAGE_TEXT[stage].hint}</span>
            </button>
            {isOpen &&
              (rows.length === 0 ? (
                <p className="border-t border-gray-200 px-4 py-3 text-sm text-gray-500 sm:px-6">No tenant in this stage.</p>
              ) : (
                <ul className="divide-y divide-gray-100 border-t border-gray-200">
                  {rows.map((tenant) => (
                    <li key={tenant.tenantId} className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-1.5 text-sm sm:px-6">
                      <Link
                        href={tenantManagementUrl(tenant.tenantId)}
                        title="Open in Tenant Management"
                        className="min-w-0 truncate text-gray-900 hover:text-blue-700 hover:underline"
                      >
                        {tenant.domainName || tenant.tenantId}
                      </Link>
                      <EditionBadge edition={planEditionOf(tenant.edition)} />
                      <span className="w-full text-xs tabular-nums text-gray-500 sm:ml-auto sm:w-auto">
                        {lifecycleFigures(stage, tenant, nowMs, portalAvailable)}
                      </span>
                    </li>
                  ))}
                </ul>
              ))}
          </section>
        );
      })}
    </div>
  );
}
