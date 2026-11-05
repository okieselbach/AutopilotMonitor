"use client";

import { useMemo } from "react";
import type { McpUsageTelemetryStatus, McpUsageToolItem } from "@/lib/generated/wire-types.generated";
import { formatChars, formatDayKey, formatDuration, formatShare, sortTools, type SortDir, type ToolSortKey } from "./mcpUsageLogic";
import { Chevron, DataNote, Figure, Pill, SortHeader } from "./parts";
import { useOpenSet, useSortState } from "./useListState";

const COLS = "lg:grid lg:grid-cols-[minmax(0,1fr)_10rem_4.5rem_4.5rem_6rem_4.5rem_4.5rem_5rem_4.5rem] lg:items-center lg:gap-4";

const DEFAULT_DIR: Record<ToolSortKey, SortDir> = { calls: "desc", requestsPerCall: "desc", errorRate: "desc", p95: "desc", size: "desc" };

export function ToolsTab({
  tools,
  telemetry,
  dateTo,
}: {
  tools: readonly McpUsageToolItem[];
  telemetry: McpUsageTelemetryStatus;
  dateTo: string;
}) {
  const { sortKey, sortDir, onSort } = useSortState<ToolSortKey>("calls", DEFAULT_DIR);
  const { open, toggle } = useOpenSet();

  const sorted = useMemo(() => sortTools(tools, sortKey, sortDir), [tools, sortKey, sortDir]);
  const maxCalls = Math.max(1, ...tools.map((t) => t.calls));

  return (
    <div className="bg-white rounded-lg shadow overflow-hidden">
      {!telemetry.requestsAvailable && (
        <DataNote>The request telemetry did not answer: calls, reach and the cost per call are missing.</DataNote>
      )}
      {!telemetry.mcpLogAvailable && (
        <DataNote>The MCP log did not answer: error rate, duration, answer size and the MCP-only tools are missing.</DataNote>
      )}
      {telemetry.mcpLogFrom && (
        <DataNote>
          Error rate, duration, answer size and the MCP-only tools cover {formatDayKey(telemetry.mcpLogFrom)} – {formatDayKey(dateTo)}.
        </DataNote>
      )}

      <div className={`hidden ${COLS} px-4 sm:px-6 py-2 border-b border-gray-200 bg-gray-50`}>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500">Tool</span>
        <SortHeader sortKey="calls" current={sortKey} dir={sortDir} onSort={onSort}>Calls</SortHeader>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500 text-right">Accounts</span>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500 text-right">Tenants</span>
        <span className="text-right" title="Backend requests one call costs the quota on average">
          <SortHeader sortKey="requestsPerCall" current={sortKey} dir={sortDir} onSort={onSort} align="right">Req./call</SortHeader>
        </span>
        <span className="text-right" title="Calls that ended as an error, soft errors included">
          <SortHeader sortKey="errorRate" current={sortKey} dir={sortDir} onSort={onSort} align="right">Errors</SortHeader>
        </span>
        <span className="text-right" title="95 % of the calls took at most this long">
          <SortHeader sortKey="p95" current={sortKey} dir={sortDir} onSort={onSort} align="right">p95</SortHeader>
        </span>
        <span className="text-right" title="Mean size of the answer the model reads">
          <SortHeader sortKey="size" current={sortKey} dir={sortDir} onSort={onSort} align="right">Size</SortHeader>
        </span>
        <span className="text-xs font-medium uppercase tracking-wider text-gray-500 text-right" title="Calls whose page exceeded the tool's size cap">
          Over cap
        </span>
      </div>

      {sorted.length === 0 ? (
        <p className="px-6 py-10 text-center text-sm text-gray-500">No tool calls in this window.</p>
      ) : (
        <ul className="divide-y divide-gray-200">
          {sorted.map((tool) => {
            const mcpOnly = tool.accounts == null;
            const expandable = tool.routes.length > 0;
            const isOpen = expandable && open.has(tool.tool);
            const share = Math.max(2, Math.round((tool.calls / maxCalls) * 100));
            const row = (
              <>
                <div className="flex min-w-0 basis-full items-center gap-2 lg:basis-auto">
                  {expandable ? <Chevron open={isOpen} /> : <span className="w-4 shrink-0" />}
                  <span className="font-mono text-sm text-gray-900 break-all sm:break-normal sm:truncate">{tool.tool}</span>
                  {mcpOnly && <Pill title="Runs in the MCP server alone: no backend request, no quota cost">MCP only</Pill>}
                </div>
                <div className="flex w-full items-center gap-2 sm:w-48 lg:w-auto">
                  <div className="h-2 flex-1 rounded-full bg-gray-100">
                    <div className="h-2 rounded-full bg-indigo-500" style={{ width: `${share}%` }} />
                  </div>
                  <span className="w-12 text-right text-sm tabular-nums text-gray-900">{tool.calls.toLocaleString()}</span>
                </div>
                <Figure label="Accounts" value={tool.accounts?.toLocaleString() ?? "—"} />
                <Figure label="Tenants" value={tool.tenants?.toLocaleString() ?? "—"} />
                <Figure label="Req./call" value={mcpOnly ? "0" : tool.requestsPerCall.toLocaleString(undefined, { maximumFractionDigits: 1 })} />
                <Figure label="Errors" value={formatShare(tool.errorRate)} tone={tool.errorRate != null && tool.errorRate >= 0.05 ? "warn" : undefined} />
                <Figure label="p95" value={formatDuration(tool.p95DurationMs)} />
                <Figure label="Size" value={formatChars(tool.avgResultChars)} />
                <Figure label="Over cap" value={tool.overCapCalls?.toLocaleString() ?? "—"} tone={tool.overCapCalls ? "warn" : undefined} />
              </>
            );
            return (
              <li key={tool.tool}>
                {expandable ? (
                  <button
                    type="button"
                    onClick={() => toggle(tool.tool)}
                    aria-expanded={isOpen}
                    className={`w-full text-left px-4 sm:px-6 py-3 hover:bg-gray-50 flex flex-wrap items-center gap-x-4 gap-y-2 ${COLS}`}
                  >
                    {row}
                  </button>
                ) : (
                  <div className={`px-4 sm:px-6 py-3 flex flex-wrap items-center gap-x-4 gap-y-2 ${COLS}`}>{row}</div>
                )}
                {isOpen && (
                  <div className="bg-gray-50 border-t border-gray-200 px-4 sm:px-6 py-2 pl-10 sm:pl-12">
                    <p className="text-xs text-gray-500 mb-1">Backend requests of its calls</p>
                    <ul className="space-y-0.5">
                      {tool.routes.map((route) => (
                        <li key={route.route} className="flex items-center justify-between gap-3 text-xs">
                          <span className="font-mono text-gray-700 break-all">{route.route}</span>
                          <span className="tabular-nums text-gray-600">{route.requests.toLocaleString()}</span>
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
