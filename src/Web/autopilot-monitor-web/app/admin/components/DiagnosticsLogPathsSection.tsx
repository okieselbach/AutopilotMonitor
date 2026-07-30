"use client";

import { useState, useMemo } from "react";
import { validateDiagnosticsPath } from "@/utils/guardValidation";
import { ValidationIndicator } from "@/components/ValidationIndicator";
import type { DiagnosticsLogPath } from "@/types/diagnostics";

// Re-export so existing consumers importing from this component keep working
export type { DiagnosticsLogPath };

interface DiagnosticsLogPathsSectionProps {
  globalDiagPaths: DiagnosticsLogPath[];
  setGlobalDiagPaths: React.Dispatch<React.SetStateAction<DiagnosticsLogPath[]>>;
  loadingConfig: boolean;
  savingDiagPaths: boolean;
  adminConfigExists: boolean;
  onSave: (paths: DiagnosticsLogPath[]) => Promise<void>;
}

export function DiagnosticsLogPathsSection({
  globalDiagPaths,
  setGlobalDiagPaths,
  loadingConfig,
  savingDiagPaths,
  adminConfigExists,
  onSave,
}: DiagnosticsLogPathsSectionProps) {
  const [newDiagPath, setNewDiagPath] = useState("");
  const [newDiagDesc, setNewDiagDesc] = useState("");
  const [newDiagSubfolders, setNewDiagSubfolders] = useState(false);

  const newPathValidation = useMemo(
    () => newDiagPath.trim() ? validateDiagnosticsPath(newDiagPath, false) : null,
    [newDiagPath]
  );

  return (
    <div className="bg-gradient-to-br from-teal-50 to-cyan-50 dark:from-gray-800 dark:to-gray-800 border-2 border-teal-300 dark:border-teal-700 rounded-lg shadow-lg">
      <div className="p-6 border-b border-teal-200 dark:border-teal-700 bg-gradient-to-r from-teal-100 to-cyan-100 dark:from-teal-900/40 dark:to-cyan-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-teal-600 dark:text-teal-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-teal-900 dark:text-teal-100">Diagnostics Log Paths</h2>
            <p className="text-sm text-teal-600 dark:text-teal-300 mt-1">Global log file paths included in diagnostics packages for all tenants. Tenants may add their own paths in Settings.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">
        {/* Info box */}
        <div className="bg-teal-50 dark:bg-teal-900/20 border border-teal-200 dark:border-teal-700 rounded-lg p-3 flex items-start space-x-2">
          <svg className="w-4 h-4 text-teal-600 dark:text-teal-400 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <p className="text-xs text-teal-700 dark:text-teal-300">
            All paths are validated on the agent against an allowlist of safe prefixes (DiagnosticsPathGuards). Wildcards are only allowed in the last path segment. Environment variables are expanded by the agent.
          </p>
        </div>

        {/* Current paths list */}
        {loadingConfig ? (
          <div className="flex items-center space-x-2 text-sm text-gray-500">
            <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-teal-600"></div>
            <span>Loading...</span>
          </div>
        ) : globalDiagPaths.length === 0 ? (
          <p className="text-sm text-gray-500 dark:text-gray-400 italic">No global paths configured yet.</p>
        ) : (
          <div className="space-y-2">
            {globalDiagPaths.map((entry, idx) => (
              <div key={idx} className="flex items-start justify-between bg-teal-100 dark:bg-teal-900/40 border border-teal-300 dark:border-teal-700 rounded-lg px-3 py-2">
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2 flex-wrap">
                    <p className="font-mono text-sm text-teal-900 dark:text-teal-100 break-all">{entry.path}</p>
                    <ValidationIndicator result={validateDiagnosticsPath(entry.path, false)} />
                    {entry.includeSubfolders && (
                      <span className="text-xs bg-teal-200 dark:bg-teal-800 text-teal-800 dark:text-teal-200 rounded-full px-1.5 py-0.5">+subfolders</span>
                    )}
                  </div>
                  {entry.description && (
                    <p className="text-xs text-teal-600 dark:text-teal-400 mt-0.5">{entry.description}</p>
                  )}
                  <label className="flex items-center gap-1.5 mt-1 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={entry.includeSubfolders || false}
                      onChange={() => {
                        const updated = [...globalDiagPaths];
                        updated[idx] = { ...entry, includeSubfolders: !entry.includeSubfolders };
                        setGlobalDiagPaths(updated);
                      }}
                      className="w-3.5 h-3.5 rounded border-teal-400 text-green-600 focus:ring-green-500"
                    />
                    <span className="text-xs text-teal-600 dark:text-teal-400">Include subfolders</span>
                  </label>
                </div>
                <button
                  onClick={() => {
                    const updated = globalDiagPaths.filter((_, i) => i !== idx);
                    setGlobalDiagPaths(updated);
                  }}
                  className="ml-3 flex-shrink-0 text-teal-500 hover:text-red-600 dark:hover:text-red-400 transition-colors"
                  title="Remove"
                >
                  <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            ))}
          </div>
        )}

        {/* Add new path */}
        <div className="flex flex-col sm:flex-row gap-2">
          <input
            type="text"
            placeholder="Path or wildcard (e.g. C:\Windows\Panther\*.log)"
            value={newDiagPath}
            onChange={(e) => setNewDiagPath(e.target.value)}
            className="flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500 font-mono"
          />
          <input
            type="text"
            placeholder="Description (optional)"
            value={newDiagDesc}
            onChange={(e) => setNewDiagDesc(e.target.value)}
            className="flex-1 px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
          />
          <button
            onClick={() => {
              const p = newDiagPath.trim().replace(/^["']+|["']+$/g, "");
              if (!p) return;
              setGlobalDiagPaths([...globalDiagPaths, { path: p, description: newDiagDesc.trim(), isBuiltIn: true, includeSubfolders: newDiagSubfolders }]);
              setNewDiagPath("");
              setNewDiagDesc("");
              setNewDiagSubfolders(false);
            }}
            disabled={!newDiagPath.trim()}
            className="px-4 py-2 bg-teal-600 text-white rounded-lg text-sm font-medium hover:bg-teal-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
          >
            Add
          </button>
        </div>
        <label className="flex items-center gap-1.5 cursor-pointer">
          <input
            type="checkbox"
            checked={newDiagSubfolders}
            onChange={() => setNewDiagSubfolders(!newDiagSubfolders)}
            className="w-3.5 h-3.5 rounded border-gray-400 text-green-600 focus:ring-green-500"
          />
          <span className="text-xs text-gray-600 dark:text-gray-400">Include subfolders</span>
        </label>
        {newPathValidation && (
          <div className="mt-1">
            <ValidationIndicator result={newPathValidation} />
          </div>
        )}

        {/* Save button */}
        <div className="flex justify-end pt-2">
          <button
            onClick={() => onSave(globalDiagPaths)}
            disabled={savingDiagPaths || !adminConfigExists}
            className="px-6 py-2 bg-gradient-to-r from-teal-600 to-cyan-600 text-white rounded-lg text-sm font-medium hover:from-teal-700 hover:to-cyan-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
          >
            {savingDiagPaths ? (
              <><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Saving...</span></>
            ) : (
              <span>Save Global Paths</span>
            )}
          </button>
        </div>
      </div>
    </div>
  );
}
