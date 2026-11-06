"use client";

import { useDeferredValue, useMemo, useState } from "react";
import Link from "next/link";
import { SearchInput } from "@/components/SearchInput";
import { QuotaBar } from "@/components/QuotaBar";
import { applicationIdOf } from "@/lib/principalKeys";
import { tenantManagementUrl } from "@/lib/routes";
import type { McpUsageAccountItem, McpUsageTenantItem } from "@/lib/generated/wire-types.generated";
import { formatDayKey } from "../../lib/format";
import { useOpenSet, useSortState, type SortDir } from "../../lib/useListState";
import { Figure, Pill, SortHeader, TenantNameCell } from "../listParts";
import { searchTenants, sortAccounts, sortTenants, type TenantInfo, type UsageSortKey } from "./mcpUsageLogic";
import { HitsPill } from "./parts";

// One grid for the header and every tenant row from lg up; below lg the rows wrap into cards.
const COLS = "lg:grid lg:grid-cols-[minmax(0,1fr)_4.5rem_4.5rem_5rem_9rem_9rem_7rem] lg:items-center lg:gap-4";

const DEFAULT_DIR: Record<UsageSortKey, SortDir> = { calls: "desc", requests: "desc", today: "desc", month: "desc", lastActive: "desc" };

function formatLastActive(iso: string | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

/** The plan an edition gets without an override — anything else is worth naming next to the badge. */
function isDefaultPlan(plan: string, info: TenantInfo | undefined): boolean {
  if (!info) return true;
  return plan === (info.edition === "community" ? "community" : "pro");
}

export function TenantsTab({ tenants, info }: { tenants: readonly McpUsageTenantItem[]; info: ReadonlyMap<string, TenantInfo> }) {
  const [query, setQuery] = useState("");
  const deferredQuery = useDeferredValue(query);
  const [nearLimitOnly, setNearLimitOnly] = useState(false);
  const { sortKey, sortDir, onSort } = useSortState<UsageSortKey>("calls", DEFAULT_DIR);
  const { open, toggle } = useOpenSet();

  const matches = useMemo(
    () => searchTenants(sortTenants(tenants, sortKey, sortDir), info, deferredQuery, nearLimitOnly),
    [tenants, info, deferredQuery, nearLimitOnly, sortKey, sortDir],
  );

  const searching = deferredQuery.trim().length > 0;

  return (
    <div className="bg-white rounded-lg shadow overflow-hidden">
      <div className="px-4 sm:px-6 py-3 border-b border-gray-200 flex flex-wrap items-center gap-3">
        <SearchInput
          value={query}
          onChange={setQuery}
          placeholder="Tenant, user or app id"
          ariaLabel="Search tenants, users and apps"
          name="mcp-usage-search"
          tone="slate"
        />
        <label className="inline-flex items-center gap-1.5 text-xs text-gray-600">
          <input type="checkbox" checked={nearLimitOnly} onChange={(e) => setNearLimitOnly(e.target.checked)} className="rounded border-gray-300" />
          Near a limit
        </label>
        <span className="text-xs text-gray-500">
          {matches.length === tenants.length ? `${tenants.length} tenants` : `${matches.length} of ${tenants.length} tenants`}
        </span>
      </div>

      <div className={`hidden ${COLS} px-4 sm:px-6 py-2 border-b border-gray-200 bg-gray-50`}>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500">Tenant</span>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500 text-right">Accounts</span>
        <span className="text-right"><SortHeader sortKey="calls" current={sortKey} dir={sortDir} onSort={onSort} align="right">Calls</SortHeader></span>
        <span className="text-right"><SortHeader sortKey="requests" current={sortKey} dir={sortDir} onSort={onSort} align="right">Requests</SortHeader></span>
        <SortHeader sortKey="today" current={sortKey} dir={sortDir} onSort={onSort}>Today</SortHeader>
        <SortHeader sortKey="month" current={sortKey} dir={sortDir} onSort={onSort}>Month</SortHeader>
        <SortHeader sortKey="lastActive" current={sortKey} dir={sortDir} onSort={onSort}>Last active</SortHeader>
      </div>

      {matches.length === 0 ? (
        <p className="px-6 py-10 text-center text-sm text-gray-500">
          {tenants.length === 0
            ? "No MCP use in this window."
            : searching
              ? `No tenant, user or app matches “${deferredQuery.trim()}”.`
              : "No tenant is near a limit."}
        </p>
      ) : (
        <ul className="divide-y divide-gray-200">
          {matches.map(({ tenant, accounts, matchedByAccount }) => {
            const t = info.get(tenant.tenantId.toLowerCase());
            const isOpen = open.has(tenant.tenantId) || matchedByAccount;
            const apps = tenant.accounts.filter((a) => applicationIdOf(a.principal)).length;
            return (
              <li key={tenant.tenantId}>
                <button
                  type="button"
                  onClick={() => toggle(tenant.tenantId)}
                  aria-expanded={isOpen}
                  className={`w-full text-left px-4 sm:px-6 py-3 hover:bg-gray-50 flex flex-wrap items-center gap-x-4 gap-y-2 ${COLS}`}
                >
                  <TenantNameCell open={isOpen} name={t?.domainName ?? tenant.tenantId} edition={t?.edition} companyName={t?.companyName}>
                    {!isDefaultPlan(tenant.quota.tenantPlan, t) && <Pill title="Usage plan set for this tenant">plan {tenant.quota.tenantPlan}</Pill>}
                    {t?.mcpDisabled && <Pill title="MCP is closed for this tenant">MCP closed</Pill>}
                    <HitsPill count={tenant.quotaHits} title="Quota refusals in this window" />
                  </TenantNameCell>
                  <Figure label="Accounts" value={apps > 0 ? `${tenant.accounts.length - apps} + ${apps} app${apps === 1 ? "" : "s"}` : String(tenant.accounts.length)} />
                  <Figure label="Calls" value={tenant.calls.toLocaleString()} />
                  <Figure label="Requests" value={tenant.requests.toLocaleString()} />
                  <div className="w-full sm:w-40 lg:w-auto">
                    <QuotaBar label="Today" used={tenant.quota.dailyUsed} limit={tenant.quota.dailyLimit} />
                  </div>
                  <div className="w-full sm:w-40 lg:w-auto">
                    <QuotaBar label="Month" used={tenant.quota.monthlyUsed} limit={tenant.quota.monthlyLimit} />
                  </div>
                  <span className="text-xs text-gray-500"><span className="lg:hidden">Last active </span>{formatLastActive(tenant.lastRequestAt)}</span>
                </button>
                {isOpen && (
                  <AccountList accounts={sortAccounts(accounts, sortKey, sortDir)} tenantPlan={tenant.quota.tenantPlan} tenantId={tenant.tenantId} />
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function AccountList({ accounts, tenantPlan, tenantId }: { accounts: readonly McpUsageAccountItem[]; tenantPlan: string; tenantId: string }) {
  return (
    <div className="bg-gray-50 border-t border-gray-200">
      <ul className="divide-y divide-gray-200">
        {accounts.map((account) => (
          <AccountRow key={account.principal} account={account} tenantPlan={tenantPlan} />
        ))}
      </ul>
      <div className="px-4 sm:px-6 py-2 text-right">
        <Link href={tenantManagementUrl(tenantId)} className="text-xs text-blue-700 hover:underline">
          Open in Tenant Management
        </Link>
      </div>
    </div>
  );
}

function AccountRow({ account, tenantPlan }: { account: McpUsageAccountItem; tenantPlan: string }) {
  const appId = applicationIdOf(account.principal);
  return (
    <li className={`px-4 sm:px-6 py-3 pl-10 sm:pl-12 flex flex-wrap items-center gap-x-4 gap-y-2 ${COLS}`}>
      <div className="min-w-0 basis-full lg:basis-auto">
        <div className="flex flex-wrap items-center gap-1.5">
          {appId && <Pill title="Service principal: an application calling with an app-only token">App</Pill>}
          <span className={`text-sm text-gray-900 break-all sm:break-normal sm:truncate ${appId ? "font-mono" : ""}`}>
            {appId ?? account.principal}
          </span>
          {account.role && <Pill title="Role on the latest request">{account.role}</Pill>}
          {account.isPlatformAdmin && <Pill title="Global Admin or Global Reader">platform</Pill>}
          {account.quota && account.quota.plan !== tenantPlan && <Pill title="Personal usage plan">plan {account.quota.plan}</Pill>}
          <HitsPill count={account.quotaHits} title="Days this account met its own quota" />
        </div>
        <div className="mt-1 flex flex-wrap items-center gap-1">
          {account.topTools.map((t) => (
            <span key={t.tool} className="rounded bg-white border border-gray-200 px-1.5 py-0.5 text-xs font-mono text-gray-600" title={`${t.calls} calls`}>
              {t.tool} <span className="text-gray-400">{t.calls}</span>
            </span>
          ))}
          {account.peakDay && (
            <span className="text-xs text-gray-500">
              busiest day {formatDayKey(account.peakDay)} with {account.peakDayRequests?.toLocaleString()} requests
            </span>
          )}
        </div>
      </div>
      <span className="hidden lg:block" />
      <Figure label="Calls" value={account.calls.toLocaleString()} />
      <Figure label="Requests" value={account.requests.toLocaleString()} />
      <div className="w-full sm:w-40 lg:w-auto">
        {account.quota ? <QuotaBar label="Today" used={account.quota.dailyUsed} limit={account.quota.dailyLimit} /> : <span className="text-xs text-gray-400">no quota state</span>}
      </div>
      <div className="w-full sm:w-40 lg:w-auto">
        {account.quota && <QuotaBar label="Month" used={account.quota.monthlyUsed} limit={account.quota.monthlyLimit} />}
      </div>
      <span className="text-xs text-gray-500"><span className="lg:hidden">Last active </span>{formatLastActive(account.lastRequestAt)}</span>
    </li>
  );
}
