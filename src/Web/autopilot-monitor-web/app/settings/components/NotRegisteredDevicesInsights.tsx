"use client";

import { useState, useEffect, useCallback } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";

interface AggregatedNotRegistered {
  serialNumber: string;
  manufacturer: string;
  model: string;
  /** Agent-reported W365 marker verdict (unverified, like all distress fields). Absent on older backends. */
  isCloudPc?: boolean;
  attemptCount: number;
  firstSeen: string;
  lastSeen: string;
}

interface DeviceNotRegisteredResponse {
  success: boolean;
  aggregated: AggregatedNotRegistered[];
  totalRawReports: number;
  dataQualityNotice: string;
}

interface NotRegisteredDevicesInsightsProps {
  getAccessToken: () => Promise<string | null>;
}

function formatDate(iso: string): string {
  try {
    return new Date(iso).toLocaleString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  } catch {
    return iso;
  }
}

export default function NotRegisteredDevicesInsights({
  getAccessToken,
}: NotRegisteredDevicesInsightsProps) {
  const [data, setData] = useState<DeviceNotRegisteredResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchData = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const res = await authenticatedFetch(api.distress.deviceNotRegistered(), getAccessToken);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const json: DeviceNotRegisteredResponse = await res.json();
      setData(json);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    fetchData();
  }, [fetchData]);

  // Loading state
  if (loading) {
    return (
      <div className="bg-white rounded-lg shadow mt-6">
        <div className="p-6">
          <div className="animate-pulse space-y-3">
            <div className="h-5 bg-gray-200 rounded w-48" />
            <div className="h-4 bg-gray-100 rounded w-full" />
            <div className="h-4 bg-gray-100 rounded w-3/4" />
          </div>
        </div>
      </div>
    );
  }

  // Error state
  if (error) {
    return (
      <div className="bg-white rounded-lg shadow mt-6">
        <div className="p-6">
          <p className="text-sm text-red-600">Failed to load unregistered devices: {error}</p>
          <button
            onClick={fetchData}
            className="mt-2 text-sm text-green-700 hover:text-green-800 underline"
          >
            Retry
          </button>
        </div>
      </div>
    );
  }

  const hasDevices = data && data.aggregated.length > 0;

  return (
    <div className="bg-white rounded-lg shadow mt-6">
      {/* Header */}
      <div className={`p-6 border-b border-gray-200 bg-gradient-to-r ${hasDevices ? "from-amber-50 to-orange-50" : "from-green-50 to-emerald-50"}`}>
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-2">
            {hasDevices ? (
              <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
            ) : (
              <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            )}
            <div>
              <h3 className={`text-lg font-semibold ${hasDevices ? "text-gray-900" : "text-green-800"}`}>
                {hasDevices ? "Devices Not Registered" : "No devices reported as not registered in the last 14 days"}
                {hasDevices && <span className="ml-2 text-xs font-normal text-gray-500">Last 14 days</span>}
              </h3>
              {hasDevices && (
                <p className="text-sm text-gray-500 mt-0.5">
                  Devices rejected with HTTP 403 because they were not found in this tenant&apos;s Autopilot or Corporate Identifier registry
                </p>
              )}
            </div>
          </div>
          {hasDevices && (
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
              Unverified
            </span>
          )}
        </div>
      </div>

      {/* Empty-state hint */}
      {!hasDevices && (
        <div className="px-6 py-4">
          <p className="text-sm text-gray-500">
            When a device attempts to enroll but is not part of your Autopilot or Corporate Identifier
            scope, it appears here. An empty list means every device that reached the backend was in scope.
          </p>
        </div>
      )}

      {/* Data quality notice + table (only when devices exist) */}
      {hasDevices && (
        <>
          <div className="px-6 pt-4 space-y-3">
            <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
              <p className="text-xs text-amber-800">
                This data comes from pre-authentication distress signals. Serial number, manufacturer,
                model, and the Cloud PC marker are self-reported and unverified. Use it to sanity-check
                whether your enrollment scoping/assignment covers these devices.
              </p>
            </div>
            {data!.aggregated.some((row) => row.isCloudPc) && (
              <div className="bg-sky-50 border border-sky-200 rounded-lg p-3">
                <p className="text-xs text-sky-800">
                  Devices marked <strong>Cloud PC</strong> identify as Windows 365 Cloud PCs. Cloud PCs are
                  never Autopilot-registered — enable <strong>Windows 365 Cloud PC Validation</strong> above
                  (and grant the <strong>W365CloudPcValidation</strong> add-on under Optional Graph
                  capabilities) so they can be admitted.
                </p>
              </div>
            )}
          </div>

          <div className="p-6">
            <div className="overflow-x-auto">
              <table className="min-w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-gray-500 uppercase tracking-wider border-b border-gray-200">
                    <th className="pb-2 pr-4">Serial Number</th>
                    <th className="pb-2 pr-4">Manufacturer</th>
                    <th className="pb-2 pr-4">Model</th>
                    <th className="pb-2 pr-4 text-right">Attempts</th>
                    <th className="pb-2 pr-4">First Seen</th>
                    <th className="pb-2">Last Seen</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {data!.aggregated.map((row, idx) => (
                    <tr key={`${row.serialNumber}|${idx}`}>
                      <td className="py-3 pr-4 font-mono text-gray-900">{row.serialNumber || "–"}</td>
                      <td className="py-3 pr-4 text-gray-700">{row.manufacturer || "–"}</td>
                      <td className="py-3 pr-4 text-gray-700">
                        {row.model || "–"}
                        {row.isCloudPc && (
                          <span
                            className="ml-2 px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full bg-sky-100 text-sky-800"
                            title="Windows 365 Cloud PC (agent-detected W365 markers, unverified)"
                          >
                            Cloud PC
                          </span>
                        )}
                      </td>
                      <td className="py-3 pr-4 text-right font-mono text-gray-900">{row.attemptCount}</td>
                      <td className="py-3 pr-4 text-gray-500 whitespace-nowrap">{formatDate(row.firstSeen)}</td>
                      <td className="py-3 text-gray-500 whitespace-nowrap">{formatDate(row.lastSeen)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="text-xs text-gray-400 mt-3">
              {data!.totalRawReports} total rejection{data!.totalRawReports !== 1 ? "s" : ""} across {data!.aggregated.length} device{data!.aggregated.length !== 1 ? "s" : ""}
            </p>
          </div>
        </>
      )}
    </div>
  );
}
