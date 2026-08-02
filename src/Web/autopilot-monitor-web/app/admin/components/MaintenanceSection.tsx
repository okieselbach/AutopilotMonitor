"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface MaintenanceSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function MaintenanceSection({
  getAccessToken,
  setError,
  setSuccessMessage,
}: MaintenanceSectionProps) {
  const [triggeringMaintenance, setTriggeringMaintenance] = useState(false);
  const [maintenanceDate, setMaintenanceDate] = useState<string>("");
  // Latest selectable day (yesterday), fixed at mount — render must stay pure
  // (react-hooks/purity) and the cap doesn't need a live midnight rollover.
  const [maxMaintenanceDate] = useState(() => new Date(Date.now() - 86400000).toISOString().split('T')[0]);
  const [reseedingRules, setReseedingRules] = useState(false);
  const [reseedingGatherRules, setReseedingGatherRules] = useState(false);
  const [reseedingImePatterns, setReseedingImePatterns] = useState(false);
  const [fetchingFromGitHub, setFetchingFromGitHub] = useState(false);

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

      // Auto-hide success message after 5 seconds
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

  const handleReseedAnalyzeRules = async () => {
    try {
      setReseedingRules(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.rules.reseedFromGitHub("analyze"), getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || `Failed to reseed analyze rules: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage(`Analyze rules reseeded from GitHub: ${result.analyze?.deleted ?? 0} deleted, ${result.analyze?.written ?? 0} written`);
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while reseeding analyze rules");
      } else {
        console.error("Error reseeding analyze rules:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to reseed analyze rules");
    } finally {
      setReseedingRules(false);
    }
  };

  const handleReseedGatherRules = async () => {
    try {
      setReseedingGatherRules(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.rules.reseedFromGitHub("gather"), getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || `Failed to reseed gather rules: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage(`Gather rules reseeded from GitHub: ${result.gather?.deleted ?? 0} deleted, ${result.gather?.written ?? 0} written`);
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while reseeding gather rules");
      } else {
        console.error("Error reseeding gather rules:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to reseed gather rules");
    } finally {
      setReseedingGatherRules(false);
    }
  };

  const handleReseedImePatterns = async () => {
    try {
      setReseedingImePatterns(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.rules.reseedFromGitHub("ime"), getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || `Failed to reseed IME log patterns: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage(`IME log patterns reseeded from GitHub: ${result.ime?.deleted ?? 0} deleted, ${result.ime?.written ?? 0} written`);
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while reseeding IME log patterns");
      } else {
        console.error("Error reseeding IME log patterns:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to reseed IME log patterns");
    } finally {
      setReseedingImePatterns(false);
    }
  };

  const handleFetchFromGitHub = async () => {
    try {
      setFetchingFromGitHub(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.rules.reseedFromGitHub("all"), getAccessToken, {
        method: "POST",
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || `Failed to reseed from GitHub: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage(
        `GitHub Reseed complete: Gather (${result.gather?.deleted ?? 0} deleted, ${result.gather?.written ?? 0} written), ` +
        `Analyze (${result.analyze?.deleted ?? 0} deleted, ${result.analyze?.written ?? 0} written), ` +
        `IME (${result.ime?.deleted ?? 0} deleted, ${result.ime?.written ?? 0} written)`
      );
      setTimeout(() => setSuccessMessage(null), 8000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while reseeding from GitHub");
      } else {
        console.error("Error reseeding from GitHub:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to reseed from GitHub");
    } finally {
      setFetchingFromGitHub(false);
    }
  };

  return (
    <>
      {/* Manual Maintenance Trigger */}
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
                  <span>Clean up ghost SessionsIndex entries <span className="text-xs text-gray-500 dark:text-gray-400">(remove after 2026-06)</span></span>
                </li>
                <li className="flex items-start">
                  <span className="text-amber-500 dark:text-amber-400 mr-2">&bull;</span>
                  <span>Backfill tenant OnboardedAt dates <span className="text-xs text-gray-500 dark:text-gray-400">(remove when complete)</span></span>
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

      {/* Fetch & Reseed from GitHub */}
      <div id="fetch-and-reseed" className="bg-gradient-to-br from-emerald-50 to-teal-50 dark:from-gray-800 dark:to-gray-800 border-2 border-emerald-300 dark:border-emerald-700 rounded-lg shadow-lg">
        <div className="p-6 border-b border-emerald-200 dark:border-emerald-700 bg-gradient-to-r from-emerald-100 to-teal-100 dark:from-emerald-900/40 dark:to-teal-900/40">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-emerald-600 dark:text-emerald-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-emerald-900 dark:text-emerald-100">Fetch &amp; Reseed from GitHub</h2>
              <p className="text-sm text-emerald-600 dark:text-emerald-300 mt-1">Fetch the latest rules from the GitHub repository and reseed all rule types</p>
            </div>
          </div>
        </div>
        <div className="p-6 space-y-4">
          <p className="text-sm text-emerald-900 dark:text-gray-200">
            This operation fetches all rule definitions from the GitHub repository and writes them to Azure Table Storage:
          </p>
          <ul className="text-sm text-emerald-900 dark:text-gray-200 space-y-1 ml-4">
            <li className="flex items-start">
              <span className="text-emerald-500 dark:text-emerald-400 mr-2">&bull;</span>
              <span>Fetches Gather Rules, Analyze Rules, and IME Log Patterns from GitHub</span>
            </li>
            <li className="flex items-start">
              <span className="text-emerald-500 dark:text-emerald-400 mr-2">&bull;</span>
              <span>Deletes all existing global built-in entries and writes the latest versions</span>
            </li>
            <li className="flex items-start">
              <span className="text-emerald-500 dark:text-emerald-400 mr-2">&bull;</span>
              <span>Tenant-specific custom rules and overrides are not affected</span>
            </li>
          </ul>
          <div className="bg-blue-50 dark:bg-blue-900/30 border border-blue-200 dark:border-blue-700 rounded-lg p-3">
            <div className="flex items-start space-x-2">
              <svg className="w-5 h-5 text-blue-500 dark:text-blue-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <p className="text-sm text-blue-800 dark:text-blue-200">
                Rules are loaded from <code className="px-1 bg-blue-100 dark:bg-blue-800 rounded text-xs">raw.githubusercontent.com</code>. After a merge to the repository, it may take up to <strong>5 minutes</strong>{" "}for GitHub&apos;s CDN cache to update. If you see stale rules after reseeding, wait a few minutes and try again.
              </p>
            </div>
          </div>
          <div className="flex justify-end pt-2">
            <button
              onClick={handleFetchFromGitHub}
              disabled={fetchingFromGitHub}
              className="px-6 py-3 bg-gradient-to-r from-emerald-600 to-teal-600 text-white rounded-lg hover:from-emerald-700 hover:to-teal-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
            >
              {fetchingFromGitHub ? (
                <>
                  <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white"></div>
                  <span>Fetching from GitHub...</span>
                </>
              ) : (
                <>
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                  <span>Fetch &amp; Reseed All</span>
                </>
              )}
            </button>
          </div>
        </div>
      </div>

      {/* Individual Reseed Sections */}
      <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
        {/* Reseed Analyze Rules */}
        <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border border-amber-200 dark:border-amber-700 rounded-lg shadow">
          <div className="p-4 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40">
            <h3 className="text-lg font-semibold text-amber-900 dark:text-amber-100">Reseed Analyze Rules</h3>
            <p className="text-xs text-amber-600 dark:text-amber-300 mt-1">Fetch and reseed only analyze rules from GitHub</p>
          </div>
          <div className="p-4">
            <div className="flex justify-end">
              <button
                onClick={handleReseedAnalyzeRules}
                disabled={reseedingRules}
                className="px-4 py-2 bg-gradient-to-r from-amber-600 to-orange-600 text-white rounded-lg hover:from-amber-700 hover:to-orange-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow flex items-center space-x-2 text-sm"
              >
                {reseedingRules ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Reseeding...</span>
                  </>
                ) : (
                  <>
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                    <span>Reseed Analyze</span>
                  </>
                )}
              </button>
            </div>
          </div>
        </div>

        {/* Reseed Gather Rules */}
        <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border border-amber-200 dark:border-amber-700 rounded-lg shadow">
          <div className="p-4 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40">
            <h3 className="text-lg font-semibold text-amber-900 dark:text-amber-100">Reseed Gather Rules</h3>
            <p className="text-xs text-amber-600 dark:text-amber-300 mt-1">Fetch and reseed only gather rules from GitHub</p>
          </div>
          <div className="p-4">
            <div className="flex justify-end">
              <button
                onClick={handleReseedGatherRules}
                disabled={reseedingGatherRules}
                className="px-4 py-2 bg-gradient-to-r from-amber-600 to-orange-600 text-white rounded-lg hover:from-amber-700 hover:to-orange-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow flex items-center space-x-2 text-sm"
              >
                {reseedingGatherRules ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Reseeding...</span>
                  </>
                ) : (
                  <>
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                    <span>Reseed Gather</span>
                  </>
                )}
              </button>
            </div>
          </div>
        </div>

        {/* Reseed IME Log Patterns */}
        <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border border-amber-200 dark:border-amber-700 rounded-lg shadow">
          <div className="p-4 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40">
            <h3 className="text-lg font-semibold text-amber-900 dark:text-amber-100">Reseed IME Log Patterns</h3>
            <p className="text-xs text-amber-600 dark:text-amber-300 mt-1">Fetch and reseed only IME log patterns from GitHub</p>
          </div>
          <div className="p-4">
            <div className="flex justify-end">
              <button
                onClick={handleReseedImePatterns}
                disabled={reseedingImePatterns}
                className="px-4 py-2 bg-gradient-to-r from-amber-600 to-orange-600 text-white rounded-lg hover:from-amber-700 hover:to-orange-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow flex items-center space-x-2 text-sm"
              >
                {reseedingImePatterns ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Reseeding...</span>
                  </>
                ) : (
                  <>
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                    <span>Reseed IME</span>
                  </>
                )}
              </button>
            </div>
          </div>
        </div>
      </div>
    </>
  );
}
