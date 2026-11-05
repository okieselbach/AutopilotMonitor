"use client";

import { useState } from "react";
import { QuotaBar } from "@/components/QuotaBar";
import { applicationIdOf } from "@/lib/principalKeys";
import type {
  McpUsageMinuteLimits,
  McpUsageQuotaHitItem,
  McpUsageTelemetryStatus,
} from "@/lib/generated/wire-types.generated";
import { formatDayKey, minuteRowsToShow, minuteShare, tenantLabel, type PlanLegendRow, type TenantInfo } from "./mcpUsageLogic";
import { DataNote, Pill } from "./parts";

function limitText(value: number | undefined): string {
  if (value == null) return "edition default";
  return value <= 0 ? "unlimited" : value.toLocaleString();
}

function timeOfDay(iso: string | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleString(undefined, { day: "numeric", month: "short", hour: "2-digit", minute: "2-digit" });
}

function Principal({ principal }: { principal: string }) {
  const appId = applicationIdOf(principal);
  return (
    <span className="inline-flex min-w-0 items-center gap-1.5">
      {appId && <Pill title="Service principal: an application calling with an app-only token">App</Pill>}
      <span className={`break-all sm:break-normal sm:truncate ${appId ? "font-mono" : ""}`}>{appId ?? principal}</span>
    </span>
  );
}

export function LimitsTab({
  quotaHits,
  minuteLimits,
  telemetry,
  plans,
  info,
}: {
  quotaHits: readonly McpUsageQuotaHitItem[];
  minuteLimits: McpUsageMinuteLimits;
  telemetry: McpUsageTelemetryStatus;
  plans: readonly PlanLegendRow[];
  info: ReadonlyMap<string, TenantInfo>;
}) {
  return (
    <div className="space-y-6">
      <QuotaSection quotaHits={quotaHits} telemetry={telemetry} plans={plans} info={info} />
      <MinuteSection minuteLimits={minuteLimits} telemetry={telemetry} info={info} />
    </div>
  );
}

function QuotaSection({
  quotaHits,
  telemetry,
  plans,
  info,
}: {
  quotaHits: readonly McpUsageQuotaHitItem[];
  telemetry: McpUsageTelemetryStatus;
  plans: readonly PlanLegendRow[];
  info: ReadonlyMap<string, TenantInfo>;
}) {
  return (
    <div className="bg-white rounded-lg shadow overflow-hidden">
      <div className="px-4 sm:px-6 py-3 border-b border-gray-200">
        <h3 className="text-sm font-medium text-gray-900">Daily and monthly quota</h3>
      </div>
      {plans.length > 0 && (
        <div className="px-4 sm:px-6 py-3 border-b border-gray-200 overflow-x-auto">
          <table className="text-xs text-gray-600">
            <thead>
              <tr className="text-left text-gray-500">
                <th className="pr-6 font-medium">Plan</th>
                <th className="pr-6 font-medium">Account per day / month</th>
                <th className="font-medium">Organization per day / month</th>
              </tr>
            </thead>
            <tbody>
              {plans.map((plan) => (
                <tr key={plan.name}>
                  <td className="pr-6 py-0.5 font-medium text-gray-900">{plan.name}</td>
                  <td className="pr-6 py-0.5 tabular-nums">{limitText(plan.daily)} / {limitText(plan.monthly)}</td>
                  <td className="py-0.5 tabular-nums">{limitText(plan.tenantDaily)} / {limitText(plan.tenantMonthly)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {!telemetry.requestsAvailable ? (
        <DataNote>The request telemetry did not answer: the refusals are missing.</DataNote>
      ) : quotaHits.length === 0 ? (
        <p className="px-6 py-8 text-center text-sm text-gray-500">No request was refused by a daily or monthly quota in this window.</p>
      ) : (
        <ul className="divide-y divide-gray-200">
          {quotaHits.map((hit) => (
            <li
              key={`${hit.date}|${hit.tenantId}|${hit.principal ?? ""}|${hit.level}|${hit.scope}`}
              className="px-4 sm:px-6 py-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-sm"
            >
              <span className="w-16 shrink-0 text-gray-500 tabular-nums">{formatDayKey(hit.date)}</span>
              <span className="min-w-0 basis-full sm:basis-auto sm:flex-1">
                <span className="text-gray-900">{hit.principal ? <Principal principal={hit.principal} /> : "—"}</span>
                <span className="ml-2 text-xs text-gray-500">{tenantLabel(hit.tenantId, info)}</span>
              </span>
              <Pill>{hit.level === "tenant" ? "Organization" : "Account"} · {hit.scope}</Pill>
              <Pill>plan {hit.plan}</Pill>
              <span className="tabular-nums text-red-700" title="The window's count at the first refusal of the day, of its limit">
                {hit.used?.toLocaleString() ?? "?"} / {hit.limit?.toLocaleString() ?? "?"}
              </span>
              <span className="text-xs text-gray-500">
                {hit.refused === 1 ? "1 request refused" : `${hit.refused.toLocaleString()} requests refused`}, {timeOfDay(hit.firstAt)}
                {hit.lastAt !== hit.firstAt && ` – ${timeOfDay(hit.lastAt)}`}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function MinuteSection({
  minuteLimits,
  telemetry,
  info,
}: {
  minuteLimits: McpUsageMinuteLimits;
  telemetry: McpUsageTelemetryStatus;
  info: ReadonlyMap<string, TenantInfo>;
}) {
  const [showAll, setShowAll] = useState(false);
  const mcpLimit = minuteLimits.mcpServerLimit;
  const close = minuteRowsToShow(minuteLimits.accounts, mcpLimit);
  const rows = showAll ? minuteLimits.accounts : close;

  return (
    <div className="bg-white rounded-lg shadow overflow-hidden">
      <div className="px-4 sm:px-6 py-3 border-b border-gray-200 flex flex-wrap items-center gap-3">
        <h3 className="text-sm font-medium text-gray-900">Per-minute limits</h3>
        <span className="text-xs text-gray-500">
          MCP server: {mcpLimit ? `${mcpLimit} messages per minute per account` : "limit not reported yet"} · Backend: per account, shown in each row
        </span>
        {minuteLimits.accounts.length > close.length && (
          <label className="ml-auto inline-flex items-center gap-1.5 text-xs text-gray-600">
            <input type="checkbox" checked={showAll} onChange={(e) => setShowAll(e.target.checked)} className="rounded border-gray-300" />
            Show all {minuteLimits.accounts.length} accounts
          </label>
        )}
      </div>
      {!telemetry.requestsAvailable && <DataNote>The request telemetry did not answer: the peaks and the backend refusals are missing.</DataNote>}
      {!telemetry.mcpLogAvailable && <DataNote>The MCP log did not answer: the MCP server&apos;s refusals and its limit are missing.</DataNote>}
      {rows.length === 0 ? (
        <p className="px-6 py-8 text-center text-sm text-gray-500">
          {minuteLimits.accounts.length === 0
            ? "No MCP requests in this window."
            : "No account came within half of a per-minute limit, and none was refused."}
        </p>
      ) : (
        <ul className="divide-y divide-gray-200">
          {rows.map((row) => (
            <li
              key={`${row.tenantId}|${row.principal}`}
              className="px-4 sm:px-6 py-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-[minmax(0,1fr)_12rem_12rem_6rem] lg:items-center"
            >
              <div className="min-w-0 text-sm text-gray-900">
                <Principal principal={row.principal} />
                <div className="text-xs text-gray-500">
                  {tenantLabel(row.tenantId, info)} · busiest 60 s at {minuteShare(row, mcpLimit)} % of a limit
                </div>
              </div>
              <div>
                {mcpLimit ? (
                  <QuotaBar label="Tool calls / 60 s" used={row.peakCalls} limit={mcpLimit} />
                ) : (
                  <span className="text-xs text-gray-600">Tool calls / 60 s: {row.peakCalls.toLocaleString()}</span>
                )}
                {row.mcpRefused > 0 && (
                  <p className="mt-1 text-xs text-red-700">{row.mcpRefused.toLocaleString()} refused by the MCP server, last {timeOfDay(row.mcpLastRefusedAt)}</p>
                )}
              </div>
              <div>
                <QuotaBar label="Requests / 60 s" used={row.peakRequests} limit={row.backendLimit} />
                {row.backendRefused > 0 && (
                  <p className="mt-1 text-xs text-red-700">{row.backendRefused.toLocaleString()} refused by the backend, last {timeOfDay(row.backendLastRefusedAt)}</p>
                )}
              </div>
              <span className="text-xs text-gray-500">{timeOfDay(row.peakRequestsAt ?? row.peakCallsAt)}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
