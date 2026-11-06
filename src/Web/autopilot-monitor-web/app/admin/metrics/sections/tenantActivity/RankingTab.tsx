"use client";

import { useDeferredValue, useMemo, useState } from "react";
import Link from "next/link";
import { SearchInput } from "@/components/SearchInput";
import { chartColors } from "@/components/charts/chartTheme";
import type { TenantActivityTenantItem } from "@/lib/generated/wire-types.generated";
import { parseUtcMs } from "@/lib/osUpdateLive";
import { tenantManagementUrl } from "@/lib/routes";
import { planEditionOf } from "../../../components/tenantFilters";
import { formatDayKey } from "../../lib/format";
import { useOpenSet, useSortState, type SortDir } from "../../lib/useListState";
import { DataNote, Figure, SortHeader, TenantNameCell } from "../listParts";
import {
  agoText,
  lastActiveMs,
  percentText,
  rankingRows,
  searchTenants,
  shareText,
  sortRanking,
  trendText,
  windowDayKeys,
  type RankingSortKey,
} from "./tenantActivityLogic";

// One grid for the header and every row from lg up; below lg the rows wrap and each figure carries its label.
const GRID = "lg:grid lg:grid-cols-[minmax(0,1fr)_6rem_4rem_4.5rem_4.5rem_5rem_5rem_7rem] lg:items-center lg:gap-4";

// Success starts with the weakest: on this column the tenants worth a look come first.
const FIRST_DIR: Record<RankingSortKey, SortDir> = {
  enrollments: "desc",
  trend: "desc",
  success: "asc",
  activeDays: "desc",
  portalUsers: "desc",
  lastActive: "desc",
};

const LAST_ACTIVITY_HINT = "Agent registration, first success or ingest — rarely a counter repair";
const LOW_SUCCESS_PERCENT = 80;
const STEEP_DROP_PERCENT = -50;

interface RankingTabProps {
  tenants: readonly TenantActivityTenantItem[];
  totalEnrollments: number;
  windowStart: string;
  windowDays: number;
  portalAvailable: boolean;
  nowMs: number;
}

/** Every tenant with enrollments in the window, sortable and searchable; a row opens its days and lifetime figures. */
export function RankingTab({ tenants, totalEnrollments, windowStart, windowDays, portalAvailable, nowMs }: RankingTabProps) {
  const [query, setQuery] = useState("");
  const deferredQuery = useDeferredValue(query);
  const { sortKey, sortDir, onSort } = useSortState<RankingSortKey>("enrollments", FIRST_DIR);
  const { open, toggle } = useOpenSet();
  const active = useMemo(() => rankingRows(tenants), [tenants]);
  const visible = useMemo(
    () => sortRanking(searchTenants(active, deferredQuery), sortKey, sortDir),
    [active, deferredQuery, sortKey, sortDir],
  );
  const dayKeys = useMemo(() => windowDayKeys(windowStart, windowDays), [windowStart, windowDays]);

  const header = (key: RankingSortKey, label: string, align: "left" | "right" = "right") => (
    <SortHeader sortKey={key} current={sortKey} dir={sortDir} onSort={onSort} align={align}>
      {label}
    </SortHeader>
  );

  return (
    <section aria-label="Tenants by enrollments" className="bg-white rounded-lg shadow overflow-hidden">
      <div className="flex flex-wrap items-center gap-3 border-b border-gray-200 px-4 py-3 sm:px-6">
        <SearchInput
          value={query}
          onChange={setQuery}
          placeholder="Domain, company or tenant id"
          ariaLabel="Search tenants"
          name="tenant-activity-search"
          tone="slate"
        />
        <span className="text-xs text-gray-500">
          {visible.length === active.length
            ? `${active.length.toLocaleString()} tenants with enrollments`
            : `${visible.length.toLocaleString()} of ${active.length.toLocaleString()} tenants`}
        </span>
      </div>
      {!portalAvailable && <DataNote>The sign-in history did not answer: the portal users are missing.</DataNote>}

      <div className={`hidden ${GRID} border-b border-gray-200 bg-gray-50 px-4 py-2 sm:px-6`}>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500">Tenant</span>
        <span className="text-right">{header("enrollments", "Enrollments")}</span>
        <span className="text-right text-xs font-medium uppercase tracking-wider text-gray-500">Share</span>
        <span className="text-right">{header("trend", "Trend")}</span>
        <span className="text-right">{header("success", "Success")}</span>
        <span className="text-right">{header("activeDays", "Active days")}</span>
        <span className="text-right">{header("portalUsers", "Portal users")}</span>
        {header("lastActive", "Last activity", "left")}
      </div>

      {visible.length === 0 ? (
        <p className="px-6 py-10 text-center text-sm text-gray-500">
          {active.length === 0 ? "No tenant enrolled a device in this window." : `No tenant matches “${deferredQuery.trim()}”.`}
        </p>
      ) : (
        <ul className="divide-y divide-gray-200">
          {visible.map((tenant) => {
            const isOpen = open.has(tenant.tenantId);
            const trendTone = tenant.trendPercent != null && tenant.trendPercent <= STEEP_DROP_PERCENT ? "warn" : undefined;
            const successTone = tenant.successPercent != null && tenant.successPercent < LOW_SUCCESS_PERCENT ? "warn" : undefined;
            return (
              <li key={tenant.tenantId}>
                <button
                  type="button"
                  onClick={() => toggle(tenant.tenantId)}
                  aria-expanded={isOpen}
                  className={`flex w-full flex-wrap items-center gap-x-4 gap-y-2 px-4 py-3 text-left hover:bg-gray-50 sm:px-6 ${GRID}`}
                >
                  <TenantNameCell
                    open={isOpen}
                    name={tenant.domainName || tenant.tenantId}
                    edition={planEditionOf(tenant.edition)}
                    companyName={tenant.companyName}
                  />
                  <Figure label="Enrollments" value={tenant.enrollments.toLocaleString()} />
                  <Figure label="Share" value={shareText(tenant.enrollments, totalEnrollments)} />
                  <Figure label="Trend" value={trendText(tenant)} tone={trendTone} />
                  <Figure label="Success" value={percentText(tenant.successPercent)} tone={successTone} />
                  <Figure label="Active days" value={`${tenant.activeDays}/${windowDays}`} />
                  <Figure label="Portal users" value={tenant.portalUsers == null ? "—" : tenant.portalUsers.toLocaleString()} />
                  <span className="text-xs text-gray-500" title={LAST_ACTIVITY_HINT}>
                    <span className="lg:hidden">Last activity </span>
                    {agoText(lastActiveMs(tenant), nowMs)}
                  </span>
                </button>
                {isOpen && <TenantDetail tenant={tenant} dayKeys={dayKeys} portalAvailable={portalAvailable} nowMs={nowMs} />}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

function TenantDetail({
  tenant,
  dayKeys,
  portalAvailable,
  nowMs,
}: {
  tenant: TenantActivityTenantItem;
  dayKeys: readonly string[];
  portalAvailable: boolean;
  nowMs: number;
}) {
  const perDay = tenant.dailyEnrollments ?? [];
  const peak = Math.max(1, ...perDay);
  const facts: Array<[string, string]> = [
    ["Enrollments since signup", tenant.lifetimeEnrollments.toLocaleString()],
    ["Succeeded since signup", tenant.lifetimeSucceeded.toLocaleString()],
    ["Events since signup", tenant.lifetimeEvents.toLocaleString()],
    ["Signed up", tenant.onboardedAt ? formatDayKey(tenant.onboardedAt.slice(0, 10), { day: "numeric", month: "short", year: "numeric" }) : "—"],
    ["First sessions", tenant.firstEnrollmentDay ? formatDayKey(tenant.firstEnrollmentDay, { day: "numeric", month: "short", year: "numeric" }) : "—"],
    ["Last portal visit", portalAvailable ? agoText(parseUtcMs(tenant.lastPortalVisitAt), nowMs) : "—"],
  ];

  return (
    <div className="space-y-3 border-t border-gray-200 bg-gray-50 px-4 py-3 sm:px-6">
      <div className="flex h-12 items-end gap-px" role="img" aria-label="Enrollments per day in this window">
        {perDay.map((count, index) => (
          <span
            key={dayKeys[index] ?? index}
            className="min-w-px flex-1 rounded-sm"
            style={{
              height: `${count > 0 ? Math.max(8, (100 * count) / peak) : 3}%`,
              backgroundColor: count > 0 ? chartColors.primary : chartColors.muted,
              opacity: count > 0 ? 1 : 0.35,
            }}
            title={`${dayKeys[index] ? formatDayKey(dayKeys[index]) : ""}: ${count.toLocaleString()}`}
          />
        ))}
      </div>
      <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-sm sm:grid-cols-3 lg:grid-cols-6">
        {facts.map(([label, value]) => (
          <div key={label}>
            <dt className="text-xs text-gray-500">{label}</dt>
            <dd className="tabular-nums text-gray-900">{value}</dd>
          </div>
        ))}
      </dl>
      <div className="text-right">
        <Link href={tenantManagementUrl(tenant.tenantId)} className="text-xs text-blue-700 hover:underline">
          Open in Tenant Management
        </Link>
      </div>
    </div>
  );
}
