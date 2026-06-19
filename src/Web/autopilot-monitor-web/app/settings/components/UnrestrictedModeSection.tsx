"use client";

import { useState } from "react";

interface UnrestrictedModeSectionProps {
  unrestrictedMode: boolean;
  setUnrestrictedMode: (value: boolean) => void;
  onSave: (value: boolean) => Promise<unknown> | void;
  saving: boolean;
}

export default function UnrestrictedModeSection({
  unrestrictedMode,
  setUnrestrictedMode,
  onSave,
  saving,
}: UnrestrictedModeSectionProps) {
  const [acknowledged, setAcknowledged] = useState(false);

  const handleToggle = () => {
    if (unrestrictedMode) {
      // Turning OFF — no confirmation needed
      setUnrestrictedMode(false);
      setAcknowledged(false);
      // Pass the new value explicitly — the React state update above is async, so onSave()
      // would otherwise persist the stale (pre-toggle) value.
      onSave(false);
    } else if (acknowledged) {
      // Turning ON with acknowledgment
      setUnrestrictedMode(true);
      onSave(true);
    }
  };

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
          <h2 className="text-lg font-semibold text-amber-900">Unrestricted Mode</h2>
        </div>
        <p className="text-sm text-amber-700 mt-1">
          Disable agent guardrails for advanced data collection
        </p>
      </div>

      <div className="p-6 space-y-4">
        {/* Active warning banner */}
        {unrestrictedMode && (
          <div className="rounded-md bg-amber-50 border border-amber-300 p-4">
            <div className="flex">
              <svg className="h-5 w-5 text-amber-500 mt-0.5 mr-3 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
              </svg>
              <div>
                <h3 className="text-sm font-semibold text-amber-800">Unrestricted Mode is active</h3>
                <p className="text-sm text-amber-700 mt-1">
                  Agent guardrails are disabled for this tenant. GatherRules can access any registry path, WMI query, command, and file path (except C:\Users).
                </p>
              </div>
            </div>
          </div>
        )}

        {/* Toggle */}
        <div className="flex items-center justify-between">
          <div>
            <label className="text-sm font-medium text-gray-700">Enable Unrestricted Mode</label>
            <p className="text-xs text-gray-500 mt-0.5">
              Removes allowlist restrictions on GatherRules and diagnostics log paths
            </p>
          </div>
          <button
            type="button"
            onClick={handleToggle}
            disabled={saving || (!unrestrictedMode && !acknowledged)}
            className={`relative inline-flex h-6 w-11 flex-shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-amber-500 focus:ring-offset-2 ${
              unrestrictedMode ? "bg-amber-500" : "bg-gray-200"
            } ${saving || (!unrestrictedMode && !acknowledged) ? "opacity-50 cursor-not-allowed" : ""}`}
          >
            <span
              className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out ${
                unrestrictedMode ? "translate-x-5" : "translate-x-0"
              }`}
            />
          </button>
        </div>

        {/* Confirmation panel — shown when OFF */}
        {!unrestrictedMode && (
          <div className="rounded-md bg-red-50 border border-red-200 p-4 space-y-3">
            <div className="flex">
              <svg className="h-5 w-5 text-red-500 mt-0.5 mr-3 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v.01M12 12V8m0 13a9 9 0 110-18 9 9 0 010 18z" />
              </svg>
              <div className="text-sm text-red-800">
                <p className="font-semibold">Warning: This disables security guardrails</p>
                <p className="mt-1">Enabling Unrestricted Mode will allow GatherRules to collect data from:</p>
                <ul className="list-disc ml-5 mt-1 space-y-0.5">
                  <li><strong>Any registry path</strong> (not just enrollment-relevant keys)</li>
                  <li><strong>Any WMI query</strong> (not just approved classes)</li>
                  <li><strong>Any PowerShell command or system command</strong></li>
                  <li><strong>Any file path</strong> (except C:\Users — always blocked for privacy)</li>
                </ul>
                <p className="mt-2 text-red-700">
                  The same relaxed rules apply to <strong>diagnostics log paths</strong>.
                </p>
              </div>
            </div>

            <label className="flex items-start space-x-2 cursor-pointer pt-2 border-t border-red-200">
              <input
                type="checkbox"
                checked={acknowledged}
                onChange={(e) => setAcknowledged(e.target.checked)}
                className="mt-0.5 h-4 w-4 rounded border-red-300 text-red-600 focus:ring-red-500"
              />
              <span className="text-sm font-medium text-red-900">
                I understand the risks and accept full responsibility for enabling Unrestricted Mode for my tenant.
              </span>
            </label>
          </div>
        )}
      </div>
    </div>
  );
}
