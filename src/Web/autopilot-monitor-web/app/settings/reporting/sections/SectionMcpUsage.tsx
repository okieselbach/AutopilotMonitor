"use client";

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

interface DailyAggregate {
  date: string;
  totalRequests: number;
  endpoints: number;
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

export function SectionMcpUsage() {
  const { getAccessToken } = useAuth();
  const [records, setRecords] = useState<UsageRecord[]>([]);
  const [usagePlan, setUsagePlan] = useState<string | null>(null);
  const [upn, setUpn] = useState<string>("");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [dateRange, setDateRange] = useState<DateRange>("30d");

  const fetchUsage = useCallback(async (range: DateRange) => {
    setLoading(true);
    setError(null);
    try {
      const dateFrom = getDateFrom(range);
      const dateTo = getDateTo();
      const res = await authenticatedFetch(
        api.mcpUsage.me(dateFrom, dateTo),
        getAccessToken
      );
      if (!res.ok) throw new Error(`Failed to fetch usage: ${res.status}`);
      const data = await res.json();
      setRecords(data.records || []);
      setUsagePlan(data.usagePlan || null);
      setUpn(data.upn || "");
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
    fetchUsage(dateRange);
  }, [fetchUsage, dateRange]);

  // Aggregate records by date
  const dailyAggregates: DailyAggregate[] = (() => {
    const byDate = new Map<string, { total: number; endpoints: Set<string> }>();
    for (const r of records) {
      const existing = byDate.get(r.date);
      if (existing) {
        existing.total += r.requestCount;
        existing.endpoints.add(r.endpoint);
      } else {
        byDate.set(r.date, { total: r.requestCount, endpoints: new Set([r.endpoint]) });
      }
    }
    return Array.from(byDate.entries())
      .map(([date, v]) => ({ date, totalRequests: v.total, endpoints: v.endpoints.size }))
      .sort((a, b) => b.date.localeCompare(a.date));
  })();

  const totalRequests = dailyAggregates.reduce((sum, d) => sum + d.totalRequests, 0);
  const todayStr = getDateTo();
  const todayRequests = dailyAggregates.find(d => d.date === todayStr)?.totalRequests ?? 0;
  const maxDaily = Math.max(...dailyAggregates.map(d => d.totalRequests), 1);

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">MCP Usage</h2>
          {upn && <p className="text-sm text-gray-500">{upn}</p>}
        </div>
        <div className="flex items-center gap-3">
          {/* Plan Badge */}
          {usagePlan && (
            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-indigo-100 text-indigo-800">
              Plan: {usagePlan}
            </span>
          )}
          {!usagePlan && (
            <span className="inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-600">
              Plan: inherited
            </span>
          )}
          {/* Date Range Selector */}
          <div className="flex rounded-md shadow-sm">
            {(["7d", "30d", "90d"] as DateRange[]).map((range) => (
              <button
                key={range}
                onClick={() => setDateRange(range)}
                className={`px-3 py-1.5 text-sm font-medium border ${
                  dateRange === range
                    ? "bg-green-600 text-white border-green-600 z-10"
                    : "bg-white text-gray-700 border-gray-300 hover:bg-gray-50"
                } ${range === "7d" ? "rounded-l-md" : ""} ${range === "90d" ? "rounded-r-md" : ""} -ml-px first:ml-0`}
              >
                {range}
              </button>
            ))}
          </div>
          <button
            onClick={() => fetchUsage(dateRange)}
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
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <div className="bg-white rounded-lg shadow p-6">
          <div className="text-sm text-gray-500 mb-1">Today&apos;s Requests</div>
          <div className="text-3xl font-bold text-indigo-600">{todayRequests.toLocaleString()}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-6">
          <div className="text-sm text-gray-500 mb-1">Total Requests ({dateRange})</div>
          <div className="text-3xl font-bold text-blue-600">{totalRequests.toLocaleString()}</div>
        </div>
        <div className="bg-white rounded-lg shadow p-6">
          <div className="text-sm text-gray-500 mb-1">Active Days</div>
          <div className="text-3xl font-bold text-green-600">{dailyAggregates.length}</div>
        </div>
      </div>

      {/* Daily Usage Chart (bar chart via divs) */}
      {dailyAggregates.length > 0 && (
        <div className="bg-white rounded-lg shadow p-6">
          <h3 className="text-sm font-medium text-gray-900 mb-4">Daily Requests</h3>
          <div className="space-y-2">
            {dailyAggregates.slice(0, 30).map((day) => (
              <div key={day.date} className="flex items-center gap-3">
                <div className="w-24 text-xs text-gray-500 font-mono shrink-0">
                  {formatDate(day.date)}
                </div>
                <div className="flex-1 bg-gray-100 rounded-full h-5 relative">
                  <div
                    className="bg-indigo-500 h-5 rounded-full transition-all"
                    style={{ width: `${Math.max((day.totalRequests / maxDaily) * 100, 2)}%` }}
                  />
                </div>
                <div className="w-16 text-xs text-gray-600 text-right shrink-0">
                  {day.totalRequests.toLocaleString()}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Empty State */}
      {!loading && records.length === 0 && !error && (
        <div className="bg-white rounded-lg shadow p-12 text-center">
          <p className="text-gray-500">No usage data found for the selected period.</p>
        </div>
      )}
    </div>
  );
}
