"use client";

import { SegmentedControl, TIME_RANGE_OPTIONS } from "@/components/SegmentedControl";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";

interface UsageRecord {
  userId: string;
  userPrincipalName: string;
  tenantId: string;
  endpoint: string;
  date: string;
  requestCount: number;
  lastRequestAt: string;
}

interface DailySummary {
  date: string;
  tenantId: string | null;
  totalRequests: number;
  uniqueUsers: number;
  uniqueEndpoints: number;
}

type DateRange = "7d" | "30d" | "90d";

function formatDate(yyyymmdd: string): string {
  if (yyyymmdd.length !== 8) return yyyymmdd;
  return `${yyyymmdd.slice(0, 4)}-${yyyymmdd.slice(4, 6)}-${yyyymmdd.slice(6, 8)}`;
}

function getDateFrom(range: DateRange): string {
  const d = new Date();
  const days = range === "7d" ? 7 : range === "30d" ? 30 : 90;
  d.setDate(d.getDate() - days);
  return d.toISOString().slice(0, 10).replace(/-/g, "");
}

function getDateTo(): string {
  return new Date().toISOString().slice(0, 10).replace(/-/g, "");
}

interface UserAggregate {
  upn: string;
  userId: string;
  tenantId: string;
  totalRequests: number;
  lastActive: string;
}

export function SectionMcpUsage() {
  const { getAccessToken } = useAuth();
  const [records, setRecords] = useState<UsageRecord[]>([]);
  const [dailySummaries, setDailySummaries] = useState<DailySummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState<DateRange>("30d");

  const fetchData = useCallback(async (range: DateRange) => {
    setLoading(true);
    setError(null);
    try {
      const dateFrom = getDateFrom(range);
      const dateTo = getDateTo();

      const [globalRes, dailyRes] = await Promise.all([
        authenticatedFetch(api.mcpUsage.global(undefined, dateFrom, dateTo), getAccessToken),
        authenticatedFetch(api.mcpUsage.daily(undefined, dateFrom, dateTo), getAccessToken),
      ]);

      if (!globalRes.ok) throw new Error(`Global usage: ${globalRes.status}`);
      if (!dailyRes.ok) throw new Error(`Daily usage: ${dailyRes.status}`);

      const globalData = await globalRes.json();
      const dailyData = await dailyRes.json();

      setRecords(globalData.records || []);
      setDailySummaries(dailyData.summaries || []);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired. Please refresh the page.");
      } else {
        setError(err instanceof Error ? err.message : "Failed to fetch usage data");
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    fetchData(dateRange);
  }, [fetchData, dateRange]);

  // Aggregate by user
  const userAggregates: UserAggregate[] = (() => {
    const byUser = new Map<string, UserAggregate>();
    for (const r of records) {
      const key = r.userId || r.userPrincipalName;
      const existing = byUser.get(key);
      if (existing) {
        existing.totalRequests += r.requestCount;
        if (r.lastRequestAt > existing.lastActive) existing.lastActive = r.lastRequestAt;
      } else {
        byUser.set(key, {
          upn: r.userPrincipalName,
          userId: r.userId,
          tenantId: r.tenantId,
          totalRequests: r.requestCount,
          lastActive: r.lastRequestAt,
        });
      }
    }
    return Array.from(byUser.values()).sort((a, b) => b.totalRequests - a.totalRequests);
  })();

  // Sorted daily summaries (newest first)
  const sortedDaily = [...dailySummaries].sort((a, b) => b.date.localeCompare(a.date));

  const totalRequests = sortedDaily.reduce((sum, d) => sum + d.totalRequests, 0);
  const uniqueUsersTotal = new Set(records.map(r => r.userId || r.userPrincipalName)).size;
  const todayStr = getDateTo();
  const todayRequests = sortedDaily.find(d => d.date === todayStr)?.totalRequests ?? 0;
  const maxDaily = Math.max(...sortedDaily.map(d => d.totalRequests), 1);

  return (
    // Self-provides the max-w container the metrics layout used to supply (the other sections
    // self-wrap with their own min-h-screen layout; this one is a plain content block).
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
      {/* Header */}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Global MCP Usage</h2>
          <p className="text-sm text-gray-500">API usage across all MCP-enabled users</p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <SegmentedControl
            options={TIME_RANGE_OPTIONS}
            value={dateRange}
            onChange={(v) => setDateRange(v as DateRange)}
          />
          <button
            onClick={() => fetchData(dateRange)}
            disabled={loading}
            className="px-3 py-1.5 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
          >
            {loading ? "Loading..." : "Refresh"}
          </button>
        </div>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 px-4 py-3 rounded-lg text-sm">
          {error}
        </div>
      )}

      {/* Summary Cards */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Today</div>
          <div className="text-2xl sm:text-3xl font-bold text-indigo-600">{todayRequests.toLocaleString()}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Total ({dateRange})</div>
          <div className="text-2xl sm:text-3xl font-bold text-blue-600">{totalRequests.toLocaleString()}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Unique Users</div>
          <div className="text-2xl sm:text-3xl font-bold text-green-600">{uniqueUsersTotal}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-4 sm:p-6">
          <div className="text-sm text-gray-500 mb-1">Active Days</div>
          <div className="text-2xl sm:text-3xl font-bold text-purple-600">{sortedDaily.length}</div>
        </div>
      </div>

      {/* Daily Usage Chart */}
      {sortedDaily.length > 0 && (
        <div className="bg-white rounded-lg shadow p-6">
          <h3 className="text-sm font-medium text-gray-900 mb-4">Daily Request Volume</h3>
          <div className="space-y-2">
            {sortedDaily.slice(0, 30).map((day) => (
              <div key={day.date} className="flex items-center gap-2 sm:gap-3">
                <div className="w-20 sm:w-24 text-xs text-gray-500 font-mono shrink-0">
                  {formatDate(day.date)}
                </div>
                <div className="flex-1 bg-gray-100 rounded-full h-5 relative">
                  <div
                    className="bg-indigo-500 h-5 rounded-full transition-all"
                    style={{ width: `${Math.max((day.totalRequests / maxDaily) * 100, 2)}%` }}
                  />
                </div>
                <div className="w-12 sm:w-24 text-xs text-gray-600 text-right shrink-0">
                  {day.totalRequests.toLocaleString()}
                  <span className="hidden sm:inline"> / {day.uniqueUsers} users</span>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Top Endpoints (aggregated across all users) */}
      {records.length > 0 && (() => {
        const endpointTotals = new Map<string, number>();
        for (const r of records) {
          endpointTotals.set(r.endpoint, (endpointTotals.get(r.endpoint) || 0) + r.requestCount);
        }
        const topEndpoints = Array.from(endpointTotals.entries())
          .sort((a, b) => b[1] - a[1])
          .slice(0, 15);
        return topEndpoints.length > 0 ? (
          <div className="bg-white rounded-lg shadow overflow-hidden">
            <div className="px-4 sm:px-6 py-4 border-b border-gray-200">
              <h3 className="text-sm font-medium text-gray-900">Top Endpoints</h3>
            </div>
            <div className="divide-y divide-gray-200">
              {topEndpoints.map(([endpoint, count]) => {
                // MCP tool calls are recorded as "toolname:/api/route" — show the
                // tool name prominently and the route as a secondary line.
                const sep = endpoint.indexOf(":");
                const tool = sep > 0 ? endpoint.slice(0, sep) : endpoint;
                const route = sep > 0 ? endpoint.slice(sep + 1) : null;
                return (
                  <div key={endpoint} className="px-4 sm:px-6 py-3 flex items-center justify-between gap-3 hover:bg-gray-50">
                    <div className="min-w-0">
                      <div className="text-sm font-mono text-gray-700 truncate">{tool}</div>
                      {route && (
                        <div className="text-xs font-mono text-gray-400 truncate">{route}</div>
                      )}
                    </div>
                    <span className="text-sm text-gray-500 shrink-0">
                      {count.toLocaleString()}
                      <span className="hidden sm:inline"> requests</span>
                    </span>
                  </div>
                );
              })}
            </div>
          </div>
        ) : null;
      })()}

      {/* Top Users */}
      {userAggregates.length > 0 && (
        <div className="bg-white rounded-lg shadow overflow-hidden">
          <div className="px-4 sm:px-6 py-4 border-b border-gray-200">
            <h3 className="text-sm font-medium text-gray-900">Top Users by Request Count</h3>
          </div>
          <div className="divide-y divide-gray-200">
            {userAggregates.slice(0, 25).map((user) => (
              <div key={user.userId || user.upn} className="px-4 sm:px-6 py-3 flex items-center justify-between gap-3 hover:bg-gray-50">
                <div className="min-w-0">
                  <div className="text-sm text-gray-900 truncate">{user.upn || user.userId}</div>
                  <div className="text-xs text-gray-500 truncate">
                    <span className="font-mono">{user.tenantId ? user.tenantId.slice(0, 8) : "—"}</span>
                    {" · "}
                    {user.lastActive ? new Date(user.lastActive).toLocaleDateString() : "—"}
                  </div>
                </div>
                <div className="text-sm font-medium text-gray-900 shrink-0">
                  {user.totalRequests.toLocaleString()}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Empty State */}
      {!loading && records.length === 0 && !error && (
        <div className="bg-white rounded-lg shadow p-12 text-center">
          <p className="text-gray-500">No MCP usage data found for the selected period.</p>
        </div>
      )}
    </div>
  );
}
