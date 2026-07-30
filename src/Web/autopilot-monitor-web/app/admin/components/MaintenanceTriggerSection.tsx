"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface MaintenanceTriggerSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function MaintenanceTriggerSection({
  getAccessToken,
  setError,
  setSuccessMessage,
}: MaintenanceTriggerSectionProps) {
  const [triggeringMaintenance, setTriggeringMaintenance] = useState(false);
  const [maintenanceDate, setMaintenanceDate] = useState<string>("");
  // Latest selectable day (yesterday), fixed at mount — render must stay pure
  // (react-hooks/purity) and the cap doesn't need a live midnight rollover.
  const [maxMaintenanceDate] = useState(() => new Date(Date.now() - 86400000).toISOString().split('T')[0]);
  const [refreshingVersions, setRefreshingVersions] = useState(false);

  const handleRefreshLatestVersions = async () => {
    try {
      setRefreshingVersions(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.config.latestVersions({ refresh: true }), getAccessToken, {
        method: "GET",
      });

      if (!response.ok) {
        throw new Error(`Failed to refresh latest versions: ${response.statusText}`);
      }

      const data = await response.json();
      const agentVer = data.latestAgentVersion ?? "unknown";
      const bootstrapVer = data.latestBootstrapScriptVersion ?? "unknown";
      const fetchedAt = data.fetchedAtUtc ? new Date(data.fetchedAtUtc).toISOString().replace("T", " ").substring(0, 19) + " UTC" : "now";
      setSuccessMessage(`Version cache refreshed: agent=${agentVer}, bootstrap=${bootstrapVer} (fetched ${fetchedAt})`);

      setTimeout(() => setSuccessMessage(null), 8000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while refreshing versions");
      } else {
        console.error("Error refreshing latest versions:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to refresh latest versions");
    } finally {
      setRefreshingVersions(false);
    }
  };

  const handleTriggerMaintenance = async () => {
    try {
      setTriggeringMaintenance(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.maintenance.trigger(maintenanceDate || undefined), getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(`Failed to trigger maintenance: ${response.statusText}`);
      }

      const dateInfo = maintenanceDate ? ` for ${maintenanceDate}` : '';
      setSuccessMessage(`Maintenance job completed successfully${dateInfo}!`);

      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while triggering maintenance");
      } else {
        console.error("Error triggering maintenance:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to trigger maintenance job");
    } finally {
      setTriggeringMaintenance(false);
    }
  };

  return (
    <div className="bg-gradient-to-br from-purple-50 to-violet-50 dark:from-gray-800 dark:to-gray-800 border-2 border-purple-300 dark:border-purple-700 rounded-lg shadow-lg">
      <div className="p-6 border-b border-purple-200 dark:border-purple-700 bg-gradient-to-r from-purple-100 to-violet-100 dark:from-purple-900/40 dark:to-violet-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-purple-600 dark:text-purple-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-purple-900 dark:text-purple-100">Manual Maintenance Trigger</h2>
            <p className="text-sm text-purple-600 dark:text-purple-300 mt-1">Execute platform-wide maintenance operations</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">
        <p className="text-sm text-purple-900 dark:text-gray-200">
          Runs all timer-triggered tasks plus additional backfill &amp; repair operations that only run on manual trigger.
        </p>
        <div className="space-y-3">
          <div>
            <p className="text-xs font-semibold text-purple-700 dark:text-purple-300 uppercase tracking-wide mb-1">Standard tasks (also run by 2h timer)</p>
            <ul className="text-sm text-purple-900 dark:text-gray-200 space-y-1 ml-4">
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Mark stalled sessions as timed out</span>
              </li>
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Aggregate metrics into historical snapshots</span>
              </li>
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Clean up old data based on retention policies</span>
              </li>
              <li className="flex items-start">
                <span className="text-purple-500 dark:text-purple-400 mr-2">&bull;</span>
                <span>Recompute platform-wide statistics</span>
              </li>
            </ul>
          </div>
          <div>
            <p className="text-xs font-semibold text-amber-700 dark:text-amber-300 uppercase tracking-wide mb-1">Manual-only tasks (backfill &amp; repair)</p>
            <ul className="text-sm text-purple-900 dark:text-gray-200 space-y-1 ml-4">
              <li className="flex items-start">
                <span className="text-amber-500 dark:text-amber-400 mr-2">&bull;</span>
                <span>Backfill sessions missing from SessionsIndex</span>
              </li>
              <li className="flex items-start">
                <span className="text-amber-500 dark:text-amber-400 mr-2">&bull;</span>
                <span>Clean up ghost SessionsIndex entries</span>
              </li>
            </ul>
          </div>
        </div>
        <div className="bg-purple-50 dark:bg-gray-700 border border-purple-200 dark:border-purple-600 rounded-lg p-4">
          <label className="block text-sm font-medium text-purple-800 dark:text-purple-200 mb-1">
            Target Date (optional)
          </label>
          <input
            type="date"
            value={maintenanceDate}
            onChange={(e) => setMaintenanceDate(e.target.value)}
            max={maxMaintenanceDate}
            className="w-full max-w-xs px-3 py-2 border border-purple-300 dark:border-purple-600 rounded-lg text-sm bg-white dark:bg-gray-600 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
          />
          <p className="text-xs text-gray-500 dark:text-gray-400 mt-2">
            Leave empty to run the standard maintenance with automatic catch-up (aggregates any missed days within the last 7 days).
            Select a specific date to manually aggregate metrics for that day, e.g. to backfill data older than 7 days.
          </p>
        </div>
        <div className="bg-white dark:bg-gray-700 border border-purple-300 dark:border-purple-600 rounded-lg p-3">
          <div className="flex items-start space-x-2">
            <svg className="w-5 h-5 text-purple-600 dark:text-purple-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <p className="text-sm text-gray-800 dark:text-gray-200">
              <strong>Warning:</strong> This operation runs across all tenants and may take several minutes to complete.
              Use this only for testing or when immediate cleanup is needed.
            </p>
          </div>
        </div>
        <div className="bg-white dark:bg-gray-700 border border-purple-300 dark:border-purple-600 rounded-lg p-4">
          <div className="flex items-start justify-between gap-4">
            <div className="flex-1">
              <p className="text-sm font-semibold text-purple-900 dark:text-purple-100 mb-1">
                Refresh Latest Agent / Bootstrap Version Cache
              </p>
              <p className="text-xs text-gray-600 dark:text-gray-400">
                Bypasses the 12h in-memory cache and re-fetches <code className="text-xs bg-purple-50 dark:bg-gray-800 px-1 py-0.5 rounded">version.json</code> from blob storage.
                Use this immediately after publishing a new agent or bootstrap script build to see the new version in session badges.
              </p>
            </div>
            <button
              onClick={handleRefreshLatestVersions}
              disabled={refreshingVersions}
              className="flex-shrink-0 px-4 py-2 bg-white dark:bg-gray-600 border border-purple-400 dark:border-purple-500 text-purple-700 dark:text-purple-200 rounded-lg hover:bg-purple-50 dark:hover:bg-gray-500 disabled:opacity-50 disabled:cursor-not-allowed transition-all text-sm font-medium flex items-center space-x-2"
            >
              {refreshingVersions ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-purple-600"></div>
                  <span>Reloading...</span>
                </>
              ) : (
                <>
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                  <span>Reload Now</span>
                </>
              )}
            </button>
          </div>
        </div>
        <div className="flex justify-end pt-2">
          <button
            onClick={handleTriggerMaintenance}
            disabled={triggeringMaintenance}
            className="px-6 py-3 bg-gradient-to-r from-purple-600 to-violet-600 text-white rounded-lg hover:from-purple-700 hover:to-violet-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
          >
            {triggeringMaintenance ? (
              <>
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white"></div>
                <span>Running...</span>
              </>
            ) : (
              <>
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span>Run Now</span>
              </>
            )}
          </button>
        </div>
      </div>
    </div>
  );
}
