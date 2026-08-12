"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { useLatest } from "@/hooks/useLatest";
import { useAuth } from "../../../contexts/AuthContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

const REFRESH_INTERVAL_MS = 30_000;
const WINDOW_OPTIONS = [5, 15, 30, 60];

interface ActiveUser {
  tenantId: string;
  upn: string;
  userRole: string;
  lastSeen: string;
  secondsAgo: number;
}

interface PresenceResponse {
  success: boolean;
  windowMinutes: number;
  activeCount: number;
  users: ActiveUser[];
}

function formatAgo(secondsAgo: number): string {
  if (secondsAgo < 60) return "just now";
  const minutes = Math.floor(secondsAgo / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h ${minutes % 60}m ago`;
}

function roleBadgeClass(role: string): string {
  switch (role) {
    case "GlobalAdmin":
      return "bg-purple-100 text-purple-700";
    case "GlobalReader":
      return "bg-indigo-100 text-indigo-700";
    case "Admin":
      return "bg-blue-100 text-blue-700";
    case "Operator":
      return "bg-teal-100 text-teal-700";
    case "Viewer":
      return "bg-gray-100 text-gray-600";
    default:
      return "bg-gray-100 text-gray-500";
  }
}

export default function PresencePage() {
  const { getAccessToken } = useAuth();
  const [data, setData] = useState<PresenceResponse | null>(null);
  const [windowMinutes, setWindowMinutes] = useState(5);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdated, setLastUpdated] = useState<Date | null>(null);

  // Keep the latest window value available to the interval callback without re-arming the timer.
  const windowRef = useLatest(windowMinutes);

  const fetchPresence = useCallback(
    async (showRefreshing = false) => {
      try {
        if (showRefreshing) setRefreshing(true);
        else setLoading(true);
        setError(null);

        const response = await authenticatedFetch(api.metrics.activeUsers(windowRef.current), getAccessToken);
        if (!response.ok) {
          throw new Error(`Failed to fetch active users: ${response.statusText}`);
        }
        const json: PresenceResponse = await response.json();
        setData(json);
        setLastUpdated(new Date());
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          console.error("Session expired:", err.message);
        } else {
          console.error("Error fetching active users:", err);
        }
        setError(err instanceof Error ? err.message : "Failed to fetch active users");
      } finally {
        setLoading(false);
        setRefreshing(false);
      }
    },
    [getAccessToken, windowRef],
  );

  // Initial load + whenever the window changes.
  useEffect(() => {
    const run = async () => {
      await fetchPresence();
    };
    void run();
  }, [fetchPresence, windowMinutes]);

  // Auto-refresh on a fixed interval (reads the current window via ref).
  useEffect(() => {
    const id = setInterval(() => fetchPresence(true), REFRESH_INTERVAL_MS);
    return () => clearInterval(id);
  }, [fetchPresence]);

  const users = data?.users ?? [];
  const activeCount = data?.activeCount ?? 0;

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          <div className="flex items-center justify-between">
            <div>
              <h1 className="text-2xl font-normal text-gray-900 dark:text-white">Active Users</h1>
              <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
                Cross-tenant — web users who made a request within the selected window
                {lastUpdated && (
                  <span className="ml-2 text-gray-400">• updated {lastUpdated.toLocaleTimeString()}</span>
                )}
              </p>
            </div>
            <div className="flex items-center space-x-3">
              <label className="text-sm text-gray-600 dark:text-gray-400">Window</label>
              <select
                value={windowMinutes}
                onChange={(e) => setWindowMinutes(Number(e.target.value))}
                className="px-3 py-2 border border-gray-200 dark:border-gray-600 rounded-lg bg-white dark:bg-gray-700 text-gray-700 dark:text-gray-200 text-sm"
              >
                {WINDOW_OPTIONS.map((m) => (
                  <option key={m} value={m}>
                    Last {m} min
                  </option>
                ))}
              </select>
              <button
                onClick={() => fetchPresence(true)}
                disabled={refreshing}
                className="px-4 py-2 bg-white dark:bg-gray-700 border border-gray-200 dark:border-gray-600 text-gray-700 dark:text-gray-200 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                <svg className={`h-5 w-5 ${refreshing ? "animate-spin" : ""}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                </svg>
                <span>{refreshing ? "Refreshing..." : "Refresh"}</span>
              </button>
            </div>
          </div>
        </div>
      </header>

      <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8 space-y-6">
        {error && (
          <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg p-4 text-sm text-red-700 dark:text-red-300">
            {error}
          </div>
        )}

        {/* Live counter */}
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6 flex items-center space-x-4">
          <span className="relative flex h-3 w-3">
            {activeCount > 0 && (
              <span className="animate-ping absolute inline-flex h-full w-full rounded-full bg-green-400 opacity-75"></span>
            )}
            <span className={`relative inline-flex rounded-full h-3 w-3 ${activeCount > 0 ? "bg-green-500" : "bg-gray-300"}`}></span>
          </span>
          <div>
            <div className="text-4xl font-bold text-gray-900 dark:text-white">
              {loading && !data ? "—" : activeCount.toLocaleString()}
            </div>
            <div className="text-sm text-gray-500 dark:text-gray-400">
              active now (last {data?.windowMinutes ?? windowMinutes} min)
            </div>
          </div>
        </div>

        {/* User list */}
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
          <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
            <thead className="bg-gray-50 dark:bg-gray-700/50">
              <tr>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">User</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Role</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Tenant</th>
                <th className="px-6 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Last seen</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
              {users.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-6 py-12 text-center text-sm text-gray-500 dark:text-gray-400">
                    {loading && !data ? "Loading…" : "No users active in this window."}
                  </td>
                </tr>
              ) : (
                users.map((u) => (
                  <tr key={`${u.tenantId}-${u.upn}`} className="hover:bg-gray-50 dark:hover:bg-gray-700/40 transition-colors">
                    <td className="px-6 py-3 text-sm font-medium text-gray-900 dark:text-white">{u.upn}</td>
                    <td className="px-6 py-3 text-sm">
                      <span className={`px-2 py-0.5 rounded text-xs font-medium ${roleBadgeClass(u.userRole)}`}>
                        {u.userRole || "Authenticated"}
                      </span>
                    </td>
                    <td className="px-6 py-3 text-sm text-gray-500 dark:text-gray-400 font-mono text-xs">{u.tenantId}</td>
                    <td className="px-6 py-3 text-sm text-gray-600 dark:text-gray-300">{formatAgo(u.secondsAgo)}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        <p className="text-xs text-gray-400 dark:text-gray-500">
          &ldquo;Active&rdquo; means the user made an authenticated request within the window. An open but idle tab
          drops off after the window elapses. Updates automatically every {REFRESH_INTERVAL_MS / 1000}s.
        </p>
      </div>
    </div>
  );
}
