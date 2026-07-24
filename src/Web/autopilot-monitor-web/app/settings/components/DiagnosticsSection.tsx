"use client";

import { useMemo, useState } from "react";
import { DiagnosticsLogPath } from "../types";
import { validateDiagnosticsPath } from "@/utils/guardValidation";
import { ValidationIndicator } from "@/components/ValidationIndicator";
import SaveResetBar from "./SaveResetBar";
import ReadOnlyFieldset from "./ReadOnlyFieldset";
import { parseSasExpiry } from "./diagnosticsSasExpiry";

// Re-export for callers that still import from the section. Implementation lives
// in `diagnosticsSasExpiry.ts` so vitest can test it without pulling in JSX.
export { parseSasExpiry };

interface DiagnosticsSectionProps {
  diagnosticsBlobSasUrl: string;
  setDiagnosticsBlobSasUrl: (value: string) => void;
  diagnosticsUploadMode: string;
  setDiagnosticsUploadMode: (value: string) => void;
  /** "CustomerSas" (default — tenant's own SAS) or "Hosted" (opt-in — backend storage). */
  diagnosticsUploadDestination: string;
  setDiagnosticsUploadDestination: (value: string) => void;
  tenantDiagPaths: DiagnosticsLogPath[];
  setTenantDiagPaths: (value: DiagnosticsLogPath[]) => void;
  globalDiagPaths: DiagnosticsLogPath[];
  newDiagPath: string;
  setNewDiagPath: (value: string) => void;
  newDiagDesc: string;
  setNewDiagDesc: (value: string) => void;
  unrestrictedMode?: boolean;
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
  /** Read-only viewer (Operator): settings visible but inert, no Save/Reset bar. */
  readOnly?: boolean;
}

export default function DiagnosticsSection({
  diagnosticsBlobSasUrl,
  setDiagnosticsBlobSasUrl,
  diagnosticsUploadMode,
  setDiagnosticsUploadMode,
  diagnosticsUploadDestination,
  setDiagnosticsUploadDestination,
  tenantDiagPaths,
  setTenantDiagPaths,
  globalDiagPaths,
  newDiagPath,
  setNewDiagPath,
  newDiagDesc,
  setNewDiagDesc,
  unrestrictedMode = false,
  onSave,
  onReset,
  saving,
  readOnly = false,
}: DiagnosticsSectionProps) {
  const isHosted = diagnosticsUploadDestination === "Hosted";
  const [newDiagSubfolders, setNewDiagSubfolders] = useState(false);

  // Compute SAS expiry directly from the current URL value so feedback is instant
  const diagnosticsSasExpiry = parseSasExpiry(diagnosticsBlobSasUrl);

  // Live validation for the "add new path" input
  const newPathValidation = useMemo(
    () => newDiagPath.trim() ? validateDiagnosticsPath(newDiagPath, unrestrictedMode) : null,
    [newDiagPath, unrestrictedMode]
  );

  return (
    <div id="diagnostics" className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Diagnostics Package</h2>
            <p className="text-sm text-gray-500 mt-1">Upload diagnostic files as a ZIP package to your Azure Blob Storage after enrollment.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">
        <ReadOnlyFieldset readOnly={readOnly}>
        <div className="space-y-4">

        {/* Destination selector — explicit admin choice between customer-controlled SAS
            (default, data stays in customer's storage) and hosted (opt-in, data leaves
            tenant boundary). Never silently flipped. */}
        <div data-testid="diagnostics-destination" className="border border-gray-200 rounded-lg p-3">
          <p className="text-gray-700 font-medium text-sm mb-2">Upload destination</p>
          <div className="flex flex-col sm:flex-row gap-2">
            <label
              className={`flex-1 flex items-start gap-2 cursor-pointer rounded-lg border p-3 transition-colors ${
                !isHosted ? "border-amber-400 bg-amber-50" : "border-gray-200 hover:border-gray-300"
              }`}
            >
              <input
                type="radio"
                name="diagDestination"
                value="CustomerSas"
                checked={!isHosted}
                onChange={() => setDiagnosticsUploadDestination("CustomerSas")}
                className="mt-1 text-amber-600 focus:ring-amber-500"
              />
              <div className="min-w-0">
                <p className="font-medium text-gray-900 text-sm">Your own Azure Blob Storage</p>
                <p className="text-xs text-gray-500 mt-0.5">
                  Data stays in your Azure tenant. You configure a Container SAS URL below.
                </p>
              </div>
            </label>
            <label
              data-testid="diagnostics-destination-hosted"
              className={`flex-1 flex items-start gap-2 cursor-pointer rounded-lg border p-3 transition-colors ${
                isHosted ? "border-sky-400 bg-sky-50" : "border-gray-200 hover:border-gray-300"
              }`}
            >
              <input
                type="radio"
                name="diagDestination"
                value="Hosted"
                checked={isHosted}
                onChange={() => setDiagnosticsUploadDestination("Hosted")}
                className="mt-1 text-sky-600 focus:ring-sky-500"
              />
              <div className="min-w-0">
                <p className="font-medium text-gray-900 text-sm flex items-center gap-1.5">
                  Hosted storage
                  <span className="inline-flex items-center px-1.5 py-0.5 rounded-full text-[10px] font-medium bg-sky-100 text-sky-800">
                    managed for you
                  </span>
                </p>
                <p className="text-xs text-gray-500 mt-0.5">
                  Uploads go to the AutopilotMonitor backend's Azure Storage. Per-upload, blob-scoped tokens; 15-min TTL.
                </p>
              </div>
            </label>
          </div>
        </div>

        {/* CustomerSas branch: existing info + SAS URL input + expiry indicator */}
        {!isHosted && (
          <>
            {/* Info */}
            <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
              <p className="text-sm text-amber-900">
                The agent requests an upload URL from the backend <strong>just before uploading</strong>. Your SAS URL is stored securely in the backend and never sent to devices in the agent configuration.
              </p>
            </div>

            {/* Blob Storage SAS URL */}
            <div data-testid="diagnostics-customersas-block">
              <label className="block">
                <span className="text-gray-700 font-medium">Blob Storage Container SAS URL</span>
                <p className="text-sm text-gray-500 mb-2">
                  Create an Azure Blob Storage container and generate a Container-level SAS URL with <strong className="text-amber-700">Read</strong>, <strong className="text-amber-700">Write</strong> and <strong className="text-amber-700">Create</strong> permissions.
                </p>
                <div className="flex items-center gap-2">
                  <input
                    type="url"
                    value={diagnosticsBlobSasUrl}
                    onChange={(e) => setDiagnosticsBlobSasUrl(e.target.value)}
                    placeholder="https://storageaccount.blob.core.windows.net/diagnostics?sv=...&sig=..."
                    className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 transition-colors font-mono text-sm"
                  />
                  {diagnosticsBlobSasUrl && diagnosticsUploadMode !== "Off" && (
                    <span className="mt-1 inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800 whitespace-nowrap">
                      Active
                    </span>
                  )}
                </div>
              </label>

              {/* SAS URL expiry indicator */}
              {diagnosticsBlobSasUrl && diagnosticsSasExpiry && (() => {
                const now = new Date();
                const daysRemaining = Math.ceil((diagnosticsSasExpiry.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
                const isExpired = daysRemaining <= 0;
                const isWarning = daysRemaining > 0 && daysRemaining <= 7;
                return (
                  <div className={`mt-2 flex items-center gap-1.5 text-xs ${isExpired ? 'text-red-600' : isWarning ? 'text-amber-600' : 'text-green-600'}`}>
                    {isExpired ? (
                      <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                      </svg>
                    ) : isWarning ? (
                      <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                      </svg>
                    ) : (
                      <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                      </svg>
                    )}
                    <span>
                      {isExpired
                        ? `Expired on ${diagnosticsSasExpiry.toLocaleDateString()}`
                        : `Expires on ${diagnosticsSasExpiry.toLocaleDateString()}${isWarning ? ` (${daysRemaining} day${daysRemaining === 1 ? '' : 's'} remaining)` : ''}`}
                    </span>
                  </div>
                );
              })()}
            </div>
          </>
        )}

        {/* Hosted branch: friendly informational block in place of the SAS input. */}
        {isHosted && (
          <div data-testid="diagnostics-hosted-block" className="bg-sky-50 border border-sky-200 rounded-lg p-4">
            <p className="font-medium text-sky-900 text-sm mb-2 flex items-center gap-1.5">
              <svg className="w-4 h-4 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              Hosted storage
            </p>
            <p className="text-sm text-sky-900 mb-2">
              Diagnostics packages are uploaded to the AutopilotMonitor backend's Azure Storage (operated by the Autopilot Monitor Team). Blobs are isolated per tenant via a <code className="font-mono text-xs bg-sky-100 px-1 rounded">&#123;tenantId&#125;/</code> prefix, and each upload uses a fresh blob-scoped, write-only token (15-min TTL).
            </p>
            <p className="text-xs text-sky-800">
              Heads-up: with this option, uploaded contents leave your own Azure tenant boundary. Retention follows your tenant's <strong>Data Retention Days</strong> setting and is enforced by the cascade-delete pipeline — old packages are removed automatically. Review the diagnostics paths section below to see what is collected.
            </p>
          </div>
        )}

        {/* Upload Mode — applies to both destinations. For CustomerSas it's gated on
            a SAS URL being present; for Hosted it's always enabled. */}
        <div className={`p-4 rounded-lg border transition-colors ${
          isHosted || diagnosticsBlobSasUrl ? 'border-gray-200 hover:border-amber-200' : 'border-gray-100 opacity-50'
        }`}>
          <div className="flex items-center justify-between">
            <div>
              <p className="font-medium text-gray-900">Upload Mode</p>
              <p className="text-sm text-gray-500">Choose when diagnostics packages are uploaded</p>
            </div>
            <select
              value={diagnosticsUploadMode}
              onChange={(e) => setDiagnosticsUploadMode(e.target.value)}
              disabled={!isHosted && !diagnosticsBlobSasUrl}
              className="px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 disabled:opacity-50 disabled:cursor-not-allowed text-sm"
            >
              <option value="Off">Off</option>
              <option value="Always">Always</option>
              <option value="OnFailure">On Failure Only</option>
            </select>
          </div>
        </div>

        {/* Additional Log Paths */}
        <div className="p-4 rounded-lg border border-gray-200">
          <p className="font-medium text-gray-900 mb-1">Additional Log Paths</p>
          <p className="text-sm text-gray-500 mb-3">
            Extra log files or wildcards included in the diagnostics ZIP. Global paths (set by your platform admin) are always included and shown below as read-only.
          </p>

          {/* Global paths (read-only) */}
          {globalDiagPaths.length > 0 && (
            <div className="mb-3">
              <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Global (platform-wide)</p>
              <div className="space-y-1.5">
                {globalDiagPaths.map((entry, idx) => (
                  <div key={idx} className="flex items-start justify-between bg-gray-100 border border-gray-300 rounded-lg px-3 py-2">
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2 flex-wrap">
                        <p className="font-mono text-xs text-gray-700 break-all">{entry.path}</p>
                        <ValidationIndicator result={validateDiagnosticsPath(entry.path, false)} />
                        {entry.includeSubfolders && (
                          <span className="text-xs bg-gray-200 text-gray-600 rounded-full px-1.5 py-0.5">+subfolders</span>
                        )}
                      </div>
                      {entry.description && <p className="text-xs text-gray-500 mt-0.5">{entry.description}</p>}
                    </div>
                    <span className="ml-2 flex-shrink-0 text-gray-400 bg-gray-200 rounded-full px-1.5 py-0.5 text-xs">global</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Tenant paths */}
          {tenantDiagPaths.length > 0 && (
            <div className="mb-3">
              <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Your paths</p>
              <div className="space-y-1.5">
                {tenantDiagPaths.map((entry, idx) => (
                  <div key={idx} className="flex items-start justify-between bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center gap-2 flex-wrap">
                        <p className="font-mono text-xs text-amber-900 break-all">{entry.path}</p>
                        <ValidationIndicator result={validateDiagnosticsPath(entry.path, unrestrictedMode)} />
                        {entry.includeSubfolders && (
                          <span className="text-xs bg-amber-200 text-amber-700 rounded-full px-1.5 py-0.5">+subfolders</span>
                        )}
                      </div>
                      {entry.description && <p className="text-xs text-amber-600 mt-0.5">{entry.description}</p>}
                      <label className="flex items-center gap-1.5 mt-1 cursor-pointer">
                        <input
                          type="checkbox"
                          checked={entry.includeSubfolders || false}
                          onChange={() => {
                            const updated = [...tenantDiagPaths];
                            updated[idx] = { ...entry, includeSubfolders: !entry.includeSubfolders };
                            setTenantDiagPaths(updated);
                          }}
                          className="w-3.5 h-3.5 rounded border-amber-400 text-amber-600 focus:ring-amber-500"
                        />
                        <span className="text-xs text-amber-600">Include subfolders</span>
                      </label>
                    </div>
                    <button
                      onClick={() => setTenantDiagPaths(tenantDiagPaths.filter((_, i) => i !== idx))}
                      className="ml-2 flex-shrink-0 text-amber-400 hover:text-red-600 transition-colors"
                      title="Remove"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                      </svg>
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Add new tenant path */}
          <div className="flex flex-col sm:flex-row gap-2 mt-2">
            <input
              type="text"
              placeholder="Path or wildcard (e.g. C:\Windows\Panther\*.log)"
              value={newDiagPath}
              onChange={(e) => setNewDiagPath(e.target.value)}
              className="flex-1 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 font-mono"
            />
            <input
              type="text"
              placeholder="Description (optional)"
              value={newDiagDesc}
              onChange={(e) => setNewDiagDesc(e.target.value)}
              className="flex-1 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
            />
            <button
              onClick={() => {
                const p = newDiagPath.trim().replace(/^["']+|["']+$/g, "");
                if (!p) return;
                setTenantDiagPaths([...tenantDiagPaths, { path: p, description: newDiagDesc.trim(), isBuiltIn: false, includeSubfolders: newDiagSubfolders }]);
                setNewDiagPath("");
                setNewDiagDesc("");
                setNewDiagSubfolders(false);
              }}
              disabled={!newDiagPath.trim()}
              className="px-4 py-1.5 bg-amber-600 text-white rounded-lg text-sm font-medium hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
            >
              Add
            </button>
          </div>
          <label className="flex items-center gap-1.5 mt-2 cursor-pointer">
            <input
              type="checkbox"
              checked={newDiagSubfolders}
              onChange={() => setNewDiagSubfolders(!newDiagSubfolders)}
              className="w-3.5 h-3.5 rounded border-gray-400 text-amber-600 focus:ring-amber-500"
            />
            <span className="text-xs text-gray-500">Include subfolders</span>
          </label>
          <div className="flex items-center gap-2 mt-2">
            <p className="text-xs text-gray-400">
              Paths are validated on the agent against an allowlist of safe prefixes. Wildcards are only allowed in the last segment.
            </p>
            <ValidationIndicator result={newPathValidation} />
          </div>
        </div>

        </div>
        </ReadOnlyFieldset>

        {!readOnly && <SaveResetBar onSave={onSave} onReset={onReset} saving={saving} />}
      </div>
    </div>
  );
}
