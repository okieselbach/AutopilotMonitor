"use client";

import SaveResetBar from "./SaveResetBar";

interface DataManagementSectionProps {
  dataRetentionDays: number;
  setDataRetentionDays: (value: number) => void;
  sessionTimeoutHours: number;
  setSessionTimeoutHours: (value: number) => void;
  isGlobalAdmin?: boolean;
  /** Edition retention cap (Community 90 / Enterprise 365) — from feature-flags entitlements. */
  retentionCapDays?: number;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
}

export default function DataManagementSection({
  dataRetentionDays,
  setDataRetentionDays,
  sessionTimeoutHours,
  setSessionTimeoutHours,
  isGlobalAdmin,
  retentionCapDays = 90,
  onSave,
  onReset,
  saving,
}: DataManagementSectionProps) {
  const isOverridden = dataRetentionDays === 0 || dataRetentionDays < 7 || dataRetentionDays > retentionCapDays;
  const isRetentionDisabled = isOverridden && !isGlobalAdmin;

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-green-50 to-emerald-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 7v10c0 2.21 3.582 4 8 4s8-1.79 8-4V7M4 7c0 2.21 3.582 4 8 4s8-1.79 8-4M4 7c0-2.21 3.582-4 8-4s8 1.79 8 4m0 5c0 2.21-3.582 4-8 4s-8-1.79-8-4" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Data Management</h2>
            <p className="text-sm text-gray-500 mt-1">Configure data retention and session timeout policies</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-6">
        {/* Data Retention Days */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Data Retention Period (Days)</span>
            <p className="text-sm text-gray-500 mb-2">
              Sessions and events older than this will be automatically deleted by the daily maintenance job. Default: 90 days.
            </p>
            <input
              type="number"
              min="7"
              max={retentionCapDays}
              value={dataRetentionDays}
              disabled={isRetentionDisabled}
              onChange={(e) => setDataRetentionDays(parseInt(e.target.value) || 90)}
              className={`mt-1 block w-full px-4 py-2 border rounded-lg placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors ${
                isRetentionDisabled
                  ? "bg-gray-100 text-gray-500 border-gray-200 cursor-not-allowed"
                  : "border-gray-300 text-gray-900"
              }`}
            />
            {isRetentionDisabled ? (
              <div className="mt-2 flex items-start space-x-2 bg-amber-50 border border-amber-200 rounded-md px-3 py-2">
                <span className="text-amber-500 mt-0.5 flex-shrink-0">⚠</span>
                <p className="text-xs text-amber-800">
                  <span className="font-semibold">Overridden by administrator</span> —{" "}
                  {dataRetentionDays === 0
                    ? "Infinite retention is active. Data will never be automatically deleted."
                    : `Retention is set to ${dataRetentionDays} days (outside the standard 7–${retentionCapDays} day range for your plan).`}
                  {" "}Contact your Global admin to change this value.
                </p>
              </div>
            ) : (
              <p className="text-xs text-gray-400 mt-1">
                Minimum: 7 days, Maximum: {retentionCapDays} days{retentionCapDays < 365 ? " (up to 365 with Enterprise)" : ""}
              </p>
            )}
          </label>
        </div>

        {/* Session Timeout Hours */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Session Timeout (Hours)</span>
            <p className="text-sm text-gray-500 mb-2">
              Sessions in "InProgress" status longer than this will be marked as "Failed - Timed Out".
              This prevents stalled sessions from running indefinitely and skewing statistics.
              <br />
              <strong>Tip:</strong> Use the same value as your ESP (Enrollment Status Page) timeout for consistency.
            </p>
            <input
              type="number"
              min="1"
              max="12"
              value={sessionTimeoutHours}
              onChange={(e) => setSessionTimeoutHours(parseInt(e.target.value) || 5)}
              className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
            />
            <p className="text-xs text-gray-400 mt-1">Default: 5 hours (ESP default). Minimum: 1 hour, Maximum: 12 hours</p>
          </label>
        </div>

        <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />
      </div>
    </div>
  );
}
