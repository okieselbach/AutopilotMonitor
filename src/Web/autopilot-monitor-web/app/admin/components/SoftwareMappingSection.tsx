"use client";

import { useCallback, useState } from "react";
import { UnmappedSoftwareTab } from "./UnmappedSoftwareTab";
import { MappedSoftwareTab } from "./MappedSoftwareTab";
import { IgnoredSoftwareTab } from "./IgnoredSoftwareTab";

interface SoftwareMappingSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function SoftwareMappingSection({
  getAccessToken,
  setError,
  setSuccessMessage,
}: SoftwareMappingSectionProps) {
  const [activeTab, setActiveTab] = useState<"unmapped" | "mapped" | "ignored">("unmapped");

  // Badge counts from child tabs
  const [unmappedCount, setUnmappedCount] = useState(0);
  const [mappedCount, setMappedCount] = useState(0);
  const [ignoredCount, setIgnoredCount] = useState(0);

  // Refresh triggers — increment to force child tab reload
  const [mappedRefresh, setMappedRefresh] = useState(0);
  const [ignoredRefresh, setIgnoredRefresh] = useState(0);
  const [unmappedRefresh, setUnmappedRefresh] = useState(0);

  const handleMappingChanged = useCallback(() => setMappedRefresh((n) => n + 1), []);
  const handleIgnored = useCallback(() => setIgnoredRefresh((n) => n + 1), []);
  const handleRestored = useCallback(() => setUnmappedRefresh((n) => n + 1), []);

  return (
    <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden border border-amber-200 dark:border-amber-800">
      {/* Header */}
      <div className="px-6 py-4 flex items-center gap-3">
        <div className="w-8 h-8 bg-gradient-to-br from-amber-100 to-orange-100 dark:from-amber-900 dark:to-orange-900 rounded-lg flex items-center justify-center">
          <svg className="w-4 h-4 text-amber-600 dark:text-amber-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
          </svg>
        </div>
        <div>
          <h2 className="text-lg font-semibold text-gray-900 dark:text-white">
            Software Mapping (Vulnerability Analyzer)
          </h2>
          <p className="text-sm text-gray-500 dark:text-gray-400">
            Manage CPE mappings for software vulnerability correlation. View unmapped software or browse and edit existing mappings.
          </p>
        </div>
      </div>

      {/* Content */}
      <div className="px-6 pb-6 border-t border-gray-200 dark:border-gray-700">
        {/* Tab Toggle */}
        <div className="flex items-center gap-1 mt-4 mb-2 bg-gray-100 dark:bg-gray-700/50 rounded-lg p-1 w-fit">
          <button
            onClick={() => setActiveTab("unmapped")}
            className={`px-4 py-1.5 text-sm font-medium rounded-md transition-colors ${
              activeTab === "unmapped"
                ? "bg-white dark:bg-gray-600 text-amber-700 dark:text-amber-300 shadow-sm"
                : "text-gray-600 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200"
            }`}
          >
            Unmapped
            {unmappedCount > 0 && activeTab !== "unmapped" && (
              <span className="ml-1.5 text-xs bg-amber-100 text-amber-700 dark:bg-amber-900/50 dark:text-amber-400 px-1.5 py-0.5 rounded-full">
                {unmappedCount}
              </span>
            )}
          </button>
          <button
            onClick={() => setActiveTab("mapped")}
            className={`px-4 py-1.5 text-sm font-medium rounded-md transition-colors ${
              activeTab === "mapped"
                ? "bg-white dark:bg-gray-600 text-amber-700 dark:text-amber-300 shadow-sm"
                : "text-gray-600 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200"
            }`}
          >
            Mapped
            {mappedCount > 0 && activeTab !== "mapped" && (
              <span className="ml-1.5 text-xs bg-gray-200 text-gray-600 dark:bg-gray-600 dark:text-gray-300 px-1.5 py-0.5 rounded-full">
                {mappedCount}
              </span>
            )}
          </button>
          <button
            onClick={() => setActiveTab("ignored")}
            className={`px-4 py-1.5 text-sm font-medium rounded-md transition-colors ${
              activeTab === "ignored"
                ? "bg-white dark:bg-gray-600 text-amber-700 dark:text-amber-300 shadow-sm"
                : "text-gray-600 dark:text-gray-400 hover:text-gray-800 dark:hover:text-gray-200"
            }`}
          >
            Ignored
            {ignoredCount > 0 && activeTab !== "ignored" && (
              <span className="ml-1.5 text-xs bg-gray-200 text-gray-600 dark:bg-gray-600 dark:text-gray-300 px-1.5 py-0.5 rounded-full">
                {ignoredCount}
              </span>
            )}
          </button>
        </div>

        {activeTab === "unmapped" && (
          <UnmappedSoftwareTab
            getAccessToken={getAccessToken}
            setError={setError}
            setSuccessMessage={setSuccessMessage}
            onMappingChanged={handleMappingChanged}
            onIgnored={handleIgnored}
            onCountChanged={setUnmappedCount}
            refreshTrigger={unmappedRefresh}
          />
        )}

        {activeTab === "mapped" && (
          <MappedSoftwareTab
            getAccessToken={getAccessToken}
            setError={setError}
            setSuccessMessage={setSuccessMessage}
            refreshTrigger={mappedRefresh}
            onCountChanged={setMappedCount}
          />
        )}

        {activeTab === "ignored" && (
          <IgnoredSoftwareTab
            getAccessToken={getAccessToken}
            setError={setError}
            refreshTrigger={ignoredRefresh}
            onRestored={handleRestored}
            onCountChanged={setIgnoredCount}
          />
        )}
      </div>
    </div>
  );
}
