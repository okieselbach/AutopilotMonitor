"use client";

import { useState, useEffect, useCallback } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";

interface AggregatedRejection {
  manufacturer: string;
  model: string;
  attemptCount: number;
  uniqueSerials: number;
  firstSeen: string;
  lastSeen: string;
  sampleSerialNumbers: string[];
}

interface HardwareRejectedResponse {
  success: boolean;
  aggregated: AggregatedRejection[];
  totalRawReports: number;
  dataQualityNotice: string;
}

interface HardwareRejectionInsightsProps {
  getAccessToken: () => Promise<string | null>;
  onAddManufacturer?: (value: string) => void;
  onAddModel?: (value: string) => void;
  webhookNotifyOnHardwareRejection: boolean;
  onToggleNotification: (value: boolean) => void;
  hasWebhook: boolean;
  /** Read-only viewer (Operator): the notify toggle is disabled (insights stay browsable). */
  readOnly?: boolean;
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

function WebhookToggle({
  checked,
  onChange,
  hasWebhook,
  disabled = false,
}: {
  checked: boolean;
  onChange: (value: boolean) => void;
  hasWebhook: boolean;
  disabled?: boolean;
}) {
  const interactive = hasWebhook && !disabled;
  return (
    <label className={`flex items-center justify-between p-3 rounded-lg border border-gray-200 transition-colors ${interactive ? "cursor-pointer hover:bg-gray-50" : "opacity-60 cursor-not-allowed"}`}>
      <div className="flex items-center space-x-2">
        <svg className="w-4 h-4 text-gray-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
        </svg>
        <div>
          <p className="text-sm font-medium text-gray-900">Webhook notifications for hardware rejections</p>
          <p className="text-xs text-gray-500">
            {hasWebhook
              ? "Send a notification to your webhook channel when a new device model is rejected (max 1 per model per 24h)"
              : "Configure a webhook in the Notifications section first to enable this feature"}
          </p>
        </div>
      </div>
      <div className="relative ml-4 flex-shrink-0">
        <input
          type="checkbox"
          checked={checked && hasWebhook}
          onChange={(e) => onChange(e.target.checked)}
          disabled={!interactive}
          className="sr-only peer"
        />
        <div className="w-9 h-5 bg-gray-200 peer-focus:outline-none peer-focus:ring-2 peer-focus:ring-blue-500 rounded-full peer peer-checked:bg-blue-600 peer-disabled:opacity-50 transition-colors" />
        <div className="absolute left-0.5 top-0.5 w-4 h-4 bg-white rounded-full shadow transition-transform peer-checked:translate-x-4" />
      </div>
    </label>
  );
}

export default function HardwareRejectionInsights({
  getAccessToken,
  onAddManufacturer,
  onAddModel,
  webhookNotifyOnHardwareRejection,
  onToggleNotification,
  hasWebhook,
  readOnly = false,
}: HardwareRejectionInsightsProps) {
  const [data, setData] = useState<HardwareRejectedResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expandedRow, setExpandedRow] = useState<string | null>(null);

  const fetchData = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const res = await authenticatedFetch(api.distress.hardwareRejected(), getAccessToken);
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const json: HardwareRejectedResponse = await res.json();
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
          <p className="text-sm text-red-600">Failed to load hardware rejections: {error}</p>
          <button
            onClick={fetchData}
            className="mt-2 text-sm text-blue-600 hover:text-blue-800 underline"
          >
            Retry
          </button>
        </div>
      </div>
    );
  }

  const hasRejections = data && data.aggregated.length > 0;

  return (
    <div className="bg-white rounded-lg shadow mt-6">
      {/* Header */}
      <div className={`p-6 border-b border-gray-200 bg-gradient-to-r ${hasRejections ? "from-amber-50 to-orange-50" : "from-green-50 to-emerald-50"}`}>
        <div className="flex items-center justify-between">
          <div className="flex items-center space-x-2">
            {hasRejections ? (
              <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
            ) : (
              <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            )}
            <div>
              <h3 className={`text-lg font-semibold ${hasRejections ? "text-gray-900" : "text-green-800"}`}>
                {hasRejections ? "Rejected Hardware" : "No hardware rejections in the last 14 days"}
                {hasRejections && <span className="ml-2 text-xs font-normal text-gray-500">Last 14 days</span>}
              </h3>
              {hasRejections && (
                <p className="text-sm text-gray-500 mt-0.5">
                  Devices that attempted to connect but were not in your whitelist
                </p>
              )}
            </div>
          </div>
          {hasRejections && (
            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-100 text-amber-800">
              Unverified
            </span>
          )}
        </div>
      </div>

      {/* Webhook notification toggle (always visible) */}
      <div className={`px-6 pt-4 ${hasRejections ? "space-y-3" : "pb-4"}`}>
        <WebhookToggle
          checked={webhookNotifyOnHardwareRejection}
          onChange={onToggleNotification}
          hasWebhook={hasWebhook}
          disabled={readOnly}
        />
      </div>

      {/* Data quality notice + table (only when rejections exist) */}
      {hasRejections && (
        <>
          <div className="px-6">
            <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
              <p className="text-xs text-amber-800">
                This data comes from pre-authentication distress signals. Manufacturer, model, and serial number values are self-reported and unverified.
              </p>
            </div>
          </div>

          <div className="p-6">
            <div className="overflow-x-auto">
              <table className="min-w-full text-sm">
                <thead>
                  <tr className="text-left text-xs text-gray-500 uppercase tracking-wider border-b border-gray-200">
                    <th className="pb-2 pr-4">Manufacturer</th>
                    <th className="pb-2 pr-4">Model</th>
                    <th className="pb-2 pr-4 text-right">Attempts</th>
                    <th className="pb-2 pr-4 text-right">Devices</th>
                    <th className="pb-2 pr-4">First Seen</th>
                    <th className="pb-2 pr-4">Last Seen</th>
                    <th className="pb-2">Actions</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {data!.aggregated.map((row) => {
                    const rowKey = `${row.manufacturer}|${row.model}`;
                    const isExpanded = expandedRow === rowKey;

                    return (
                      <tr key={rowKey} className="group">
                        <td className="py-3 pr-4">
                          <button
                            onClick={() => setExpandedRow(isExpanded ? null : rowKey)}
                            className="text-left font-medium text-gray-900 hover:text-blue-600 transition-colors"
                            title="Toggle details"
                          >
                            {row.manufacturer || "\u2013"}
                            {row.sampleSerialNumbers.length > 0 && (
                              <svg
                                className={`inline-block w-3.5 h-3.5 ml-1 text-gray-400 transition-transform ${isExpanded ? "rotate-90" : ""}`}
                                fill="none"
                                stroke="currentColor"
                                viewBox="0 0 24 24"
                              >
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                              </svg>
                            )}
                          </button>
                          {isExpanded && row.sampleSerialNumbers.length > 0 && (
                            <div className="mt-2 pl-2 border-l-2 border-amber-200">
                              <p className="text-xs text-gray-500 mb-1">Serial Numbers:</p>
                              {row.sampleSerialNumbers.map((sn) => (
                                <p key={sn} className="text-xs font-mono text-gray-600">{sn}</p>
                              ))}
                              {row.uniqueSerials > row.sampleSerialNumbers.length && (
                                <p className="text-xs text-gray-400 mt-1">
                                  +{row.uniqueSerials - row.sampleSerialNumbers.length} more
                                </p>
                              )}
                            </div>
                          )}
                        </td>
                        <td className="py-3 pr-4 text-gray-700">{row.model || "\u2013"}</td>
                        <td className="py-3 pr-4 text-right font-mono text-gray-900">{row.attemptCount}</td>
                        <td className="py-3 pr-4 text-right font-mono text-gray-600">{row.uniqueSerials}</td>
                        <td className="py-3 pr-4 text-gray-500 whitespace-nowrap">{formatDate(row.firstSeen)}</td>
                        <td className="py-3 pr-4 text-gray-500 whitespace-nowrap">{formatDate(row.lastSeen)}</td>
                        <td className="py-3">
                          <div className="flex gap-1">
                            {onAddManufacturer && row.manufacturer && (
                              <button
                                onClick={() => onAddManufacturer(row.manufacturer)}
                                className="px-2 py-1 text-xs bg-blue-50 text-blue-700 rounded hover:bg-blue-100 transition-colors whitespace-nowrap"
                                title={`Add "${row.manufacturer}" to manufacturer whitelist`}
                              >
                                + Mfr
                              </button>
                            )}
                            {onAddModel && row.model && (
                              <button
                                onClick={() => onAddModel(row.model)}
                                className="px-2 py-1 text-xs bg-indigo-50 text-indigo-700 rounded hover:bg-indigo-100 transition-colors whitespace-nowrap"
                                title={`Add "${row.model}" to model whitelist`}
                              >
                                + Model
                              </button>
                            )}
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
            <p className="text-xs text-gray-400 mt-3">
              {data!.totalRawReports} total rejection{data!.totalRawReports !== 1 ? "s" : ""} across {data!.aggregated.length} hardware combination{data!.aggregated.length !== 1 ? "s" : ""}
            </p>
          </div>
        </>
      )}
    </div>
  );
}
