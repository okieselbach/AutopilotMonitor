"use client";

import { useState, useMemo } from "react";
import { validateDiagnosticsPath } from "@/utils/guardValidation";
import { ValidationIndicator } from "@/components/ValidationIndicator";
import { BuiltInSectionsList } from "@/components/diagnostics/BuiltInSectionsList";
import { DiagnosticsPathRow } from "@/components/diagnostics/DiagnosticsPathRow";
import type { DiagnosticsBuiltInSection, DiagnosticsLogPath } from "@/types/diagnostics";
import { SectionCardHeader } from "@/components/SectionCardHeader";

// Re-export so existing consumers importing from this component keep working
export type { DiagnosticsLogPath };

interface DiagnosticsLogPathsSectionProps {
  globalDiagPaths: DiagnosticsLogPath[];
  setGlobalDiagPaths: React.Dispatch<React.SetStateAction<DiagnosticsLogPath[]>>;
  /** Sections compiled into the agent (GET /api/diagnostics/paths) — shown read-only, collapsed. */
  builtInSections: DiagnosticsBuiltInSection[];
  builtInLoading: boolean;
  loadingConfig: boolean;
  savingDiagPaths: boolean;
  adminConfigExists: boolean;
  onSave: (paths: DiagnosticsLogPath[]) => Promise<void>;
}

export function DiagnosticsLogPathsSection({
  globalDiagPaths,
  setGlobalDiagPaths,
  builtInSections,
  builtInLoading,
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

  const addPath = () => {
    const p = newDiagPath.trim().replace(/^["']+|["']+$/g, "");
    if (!p) return;
    setGlobalDiagPaths([...globalDiagPaths, { path: p, description: newDiagDesc.trim(), isBuiltIn: true, includeSubfolders: newDiagSubfolders }]);
    setNewDiagPath("");
    setNewDiagDesc("");
    setNewDiagSubfolders(false);
  };

  return (
    <div className="bg-gradient-to-br from-teal-50 to-cyan-50 dark:from-gray-800 dark:to-gray-800 border-2 border-teal-300 dark:border-teal-700 rounded-lg shadow-lg">
      <SectionCardHeader
        tone="adminTeal"
        iconPath="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
        title="Diagnostics Log Paths"
        subtitle="What every diagnostics package collects: the built-in sections compiled into the agent, plus the global paths below sent to all tenants. Tenants may add their own paths in Settings."
      />
      <div className="p-6 space-y-4">
        {/* Info box */}
        <div className="bg-teal-50 dark:bg-teal-900/20 border border-teal-200 dark:border-teal-700 rounded-lg p-3 flex items-start space-x-2">
          <svg className="w-4 h-4 text-teal-600 dark:text-teal-400 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <p className="text-xs text-teal-700 dark:text-teal-300">
            Global paths are validated on the agent against an allowlist of safe prefixes (DiagnosticsPathGuards). Wildcards are only allowed in the last path segment. Environment variables and %LOGGED_ON_USER_PROFILE% are expanded by the agent.
          </p>
        </div>

        {/* Built-in sections (read-only, collapsed) */}
        <BuiltInSectionsList sections={builtInSections} loading={builtInLoading} />

        {/* Global paths — add-row first so a long list never pushes the input out of reach */}
        <div>
          <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Global (all tenants)</p>
          <div className="flex flex-col sm:flex-row gap-2">
            <input
              type="text"
              placeholder="Path or wildcard (e.g. C:\Windows\Panther\*.log)"
              value={newDiagPath}
              onChange={(e) => setNewDiagPath(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addPath(); } }}
              className="flex-1 px-3 py-1.5 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500 font-mono"
            />
            <input
              type="text"
              placeholder="Description (optional)"
              value={newDiagDesc}
              onChange={(e) => setNewDiagDesc(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addPath(); } }}
              className="flex-1 px-3 py-1.5 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
            />
            <label className="flex items-center gap-1.5 whitespace-nowrap text-xs text-gray-600 dark:text-gray-400 cursor-pointer">
              <input
                type="checkbox"
                checked={newDiagSubfolders}
                onChange={() => setNewDiagSubfolders(!newDiagSubfolders)}
                className="w-3.5 h-3.5 rounded border-gray-400 text-green-600 focus:ring-green-500"
              />
              subfolders
            </label>
            <button
              onClick={addPath}
              disabled={!newDiagPath.trim()}
              className="px-4 py-1.5 bg-teal-600 text-white rounded-lg text-sm font-medium hover:bg-teal-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
            >
              Add
            </button>
          </div>
          {newPathValidation && (
            <div className="mt-1.5">
              <ValidationIndicator result={newPathValidation} />
            </div>
          )}

          {loadingConfig ? (
            <div className="mt-3 flex items-center space-x-2 text-sm text-gray-500">
              <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-teal-600"></div>
              <span>Loading...</span>
            </div>
          ) : globalDiagPaths.length === 0 ? (
            <p className="mt-3 text-sm text-gray-500 dark:text-gray-400 italic">
              No global paths configured yet — every tenant receives only the built-in collection.
            </p>
          ) : (
            <div className="mt-3 space-y-1">
              {globalDiagPaths.map((entry, idx) => (
                <DiagnosticsPathRow
                  key={`${entry.path}-${idx}`}
                  path={entry.path}
                  description={entry.description}
                  includeSubfolders={entry.includeSubfolders || false}
                  validation={validateDiagnosticsPath(entry.path, false)}
                  className="bg-teal-100 dark:bg-teal-900/40 border-teal-300 dark:border-teal-700"
                  pathClassName="text-teal-900 dark:text-teal-100"
                  onToggleSubfolders={() => {
                    const updated = [...globalDiagPaths];
                    updated[idx] = { ...entry, includeSubfolders: !entry.includeSubfolders };
                    setGlobalDiagPaths(updated);
                  }}
                  onRemove={() => setGlobalDiagPaths(globalDiagPaths.filter((_, i) => i !== idx))}
                />
              ))}
            </div>
          )}
        </div>

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
