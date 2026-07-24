"use client";

import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import TruncatedLabel from "@/components/TruncatedLabel";
import { useSignalR } from "../../contexts/SignalRContext";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useProgressSearch } from "./hooks/useProgressSearch";
import { useProgressEvents } from "./hooks/useProgressEvents";
import { useProgressSignalR } from "./hooks/useProgressSignalR";
import { useProgressDerivedData } from "./hooks/useProgressDerivedData";

function formatDuration(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) return `${minutes}m ${remainingSeconds}s`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return `${hours}h ${remainingMinutes}m`;
}

const phaseSteps = [
  { id: 0, label: "Setup start", shortLabel: "Start" },
  { id: 1, label: "Device preparation", shortLabel: "Preparation" },
  { id: 2, label: "Device setup", shortLabel: "Device" },
  { id: 3, label: "Installing apps (device)", shortLabel: "Apps (D)" },
  { id: 4, label: "Account setup", shortLabel: "Account" },
  { id: 5, label: "Installing apps (user)", shortLabel: "Apps (U)" },
  { id: 6, label: "Finalizing setup", shortLabel: "Complete" },
];

export default function ProgressPortalPage() {
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const signalR = useSignalR();

  const search = useProgressSearch({
    tenantId,
    getAccessToken,
    addNotification,
  });
  const {
    serialInput,
    setSerialInput,
    session,
    setSession,
    searching,
    notFound,
    headerCollapsed,
    setHeaderCollapsed,
    searchBySerial,
  } = search;

  const { events, sessionRef, scheduleFetchEvents } = useProgressEvents({
    session,
    setSession,
    tenantId,
    getAccessToken,
    addNotification,
  });

  useProgressSignalR({
    session,
    sessionRef,
    signalR,
    scheduleFetchEvents,
  });

  const { appSummary, currentDownload, currentInstall, installElapsedMs, overallProgress } =
    useProgressDerivedData(events, session);

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") searchBySerial();
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <div className="max-w-2xl mx-auto px-4 py-6 sm:py-12">
          {/* Collapsible Header + Search */}
          {headerCollapsed && session ? (
            <button
              onClick={() => setHeaderCollapsed(false)}
              className="w-full flex items-center justify-between bg-white rounded-xl shadow-sm border border-gray-200 px-4 py-2.5 mb-4 hover:bg-gray-50 transition-colors"
            >
              <div className="flex items-center space-x-2 min-w-0">
                <svg
                  className="w-4 h-4 text-blue-600 flex-shrink-0"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                  />
                </svg>
                <span className="text-sm font-medium text-gray-700">
                  Device Setup Progress
                </span>
              </div>
              <div className="flex items-center space-x-1.5 flex-shrink-0 ml-3">
                <span className="text-xs text-blue-600 font-medium">Change device</span>
                <svg
                  className="w-3.5 h-3.5 text-blue-600"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M19 9l-7 7-7-7"
                  />
                </svg>
              </div>
            </button>
          ) : (
            <>
              {/* Full Header */}
              <div className="text-center mb-10">
                {session && (
                  <button
                    onClick={() => setHeaderCollapsed(true)}
                    className="mb-2 text-xs text-gray-400 hover:text-gray-600 transition-colors flex items-center justify-center mx-auto space-x-1"
                  >
                    <svg
                      className="w-3 h-3"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M5 15l7-7 7 7"
                      />
                    </svg>
                    <span>Collapse</span>
                  </button>
                )}
                <div className="inline-flex items-center justify-center w-16 h-16 bg-blue-100 rounded-full mb-4">
                  <svg
                    className="w-8 h-8 text-blue-600"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                    />
                  </svg>
                </div>
                <h1 className="text-2xl font-normal text-gray-900 mb-2">
                  Device Setup Progress
                </h1>
                <p className="text-gray-500">
                  Enter your device serial number to check status
                </p>
              </div>

              {/* Search */}
              <div className="flex items-center space-x-3 mb-10">
                <div className="flex-1 relative">
                  <input
                    type="text"
                    value={serialInput}
                    onChange={(e) => setSerialInput(e.target.value)}
                    onKeyDown={handleKeyDown}
                    placeholder="Enter serial number or device name..."
                    className="w-full px-4 py-3 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-lg"
                  />
                </div>
                <button
                  onClick={searchBySerial}
                  disabled={searching || !serialInput.trim()}
                  className="px-6 py-3 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed font-medium"
                >
                  {searching ? "Searching..." : "Check Status"}
                </button>
              </div>
            </>
          )}

          {/* Not Found */}
          {notFound && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-8 text-center">
              <svg
                className="w-12 h-12 mx-auto text-gray-300 mb-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={1.5}
                  d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                />
              </svg>
              <h2 className="text-lg font-semibold text-gray-900 mb-2">
                Device Not Found
              </h2>
              <p className="text-gray-500 text-sm">
                No enrollment session found for &quot;{serialInput}&quot;.
                Please check the serial number and try again.
              </p>
            </div>
          )}

          {/* Session Found - Progress Display */}
          {session && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
              {/* Status Header */}
              <div
                className={`px-6 py-4 ${
                  session.status === "InProgress"
                    ? "bg-blue-50 border-b border-blue-100"
                    : session.status === "Succeeded"
                    ? "bg-green-50 border-b border-green-100"
                    : "bg-red-50 border-b border-red-100"
                }`}
              >
                <div className="text-center">
                  <h2
                    className={`text-xl font-semibold ${
                      session.status === "InProgress"
                        ? "text-blue-800"
                        : session.status === "Succeeded"
                        ? "text-green-800"
                        : "text-red-800"
                    }`}
                  >
                    {session.status === "InProgress"
                      ? "Setting up your device..."
                      : session.status === "Succeeded"
                      ? "Setup complete!"
                      : "Setup encountered an issue"}
                  </h2>
                  <p className="text-sm text-gray-500 mt-1">
                    {session.deviceName || session.serialNumber} |{" "}
                    {session.manufacturer} {session.model}
                  </p>
                </div>
              </div>

              <div className="p-6">
                {/* Overall Progress Bar */}
                <div className="mb-8">
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm text-gray-500">
                      Overall Progress
                    </span>
                    <span
                      className={`text-sm font-semibold ${
                        session.status === "Failed"
                          ? "text-red-600"
                          : "text-blue-600"
                      }`}
                    >
                      {Math.round(overallProgress)}%
                    </span>
                  </div>
                  <div className="w-full h-4 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all duration-1000 ${
                        session.status === "Failed"
                          ? "bg-red-500"
                          : session.status === "Succeeded"
                          ? "bg-green-500"
                          : "bg-blue-500"
                      }`}
                      style={{ width: `${overallProgress}%` }}
                    />
                  </div>
                </div>

                {/* Phase Steps */}
                <div className="space-y-3 mb-8">
                  {phaseSteps.map((step) => {
                    const effectivePhase =
                      session.currentPhase === 99
                        ? 3
                        : session.currentPhase;
                    const isCompleted =
                      (session.status === "Succeeded" && step.id <= 6) ||
                      step.id < effectivePhase;
                    const isCurrent =
                      step.id === effectivePhase &&
                      session.status === "InProgress";
                    const isFailed =
                      step.id === effectivePhase &&
                      session.status === "Failed";

                    return (
                      <div key={step.id}>
                        <div className="flex items-center space-x-3">
                          {/* Icon */}
                          <div className="flex-shrink-0">
                            {isCompleted ? (
                              <div className="w-8 h-8 rounded-full bg-green-100 flex items-center justify-center">
                                <svg
                                  className="w-5 h-5 text-green-600"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    strokeWidth={3}
                                    d="M5 13l4 4L19 7"
                                  />
                                </svg>
                              </div>
                            ) : isCurrent ? (
                              <div className="w-8 h-8 rounded-full bg-blue-100 flex items-center justify-center">
                                <div className="w-3 h-3 bg-blue-500 rounded-full animate-pulse" />
                              </div>
                            ) : isFailed ? (
                              <div className="w-8 h-8 rounded-full bg-red-100 flex items-center justify-center">
                                <svg
                                  className="w-5 h-5 text-red-600"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    strokeWidth={3}
                                    d="M6 18L18 6M6 6l12 12"
                                  />
                                </svg>
                              </div>
                            ) : (
                              <div className="w-8 h-8 rounded-full bg-gray-100 flex items-center justify-center">
                                <div className="w-3 h-3 bg-gray-300 rounded-full" />
                              </div>
                            )}
                          </div>

                          {/* Label */}
                          <div className="min-w-0">
                            <span
                              className={`text-sm ${
                                isCompleted
                                  ? "text-green-700 font-medium"
                                  : isCurrent
                                  ? "text-blue-700 font-medium"
                                  : isFailed
                                  ? "text-red-700 font-medium"
                                  : "text-gray-400"
                              }`}
                            >
                              {step.label}
                              {isCurrent &&
                                (step.id === 3 || step.id === 5) &&
                                appSummary &&
                                appSummary.total > 0 &&
                                ` (${appSummary.installed}/${appSummary.total})`}
                            </span>
                            {/* Current activity detail below the active step */}
                            {isCurrent && (step.id === 3 || step.id === 5) && (
                              <div className="flex items-center space-x-1.5 mt-0.5">
                                <div className="w-1.5 h-1.5 bg-blue-400 rounded-full animate-pulse flex-shrink-0" />
                                <TruncatedLabel
                                  className="text-xs text-blue-500"
                                  text={
                                    currentDownload?.active && currentDownload.appName
                                      ? `Downloading ${currentDownload.appName}...`
                                      : currentInstall?.active && currentInstall.appName
                                      ? `Installing ${currentInstall.appName}...`
                                      : "Processing..."
                                  }
                                />
                              </div>
                            )}
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>

                {/* Activity Details — visible during app install phases */}
                {session.status === "InProgress" && (session.currentPhase === 3 || session.currentPhase === 5) && (appSummary || currentDownload || currentInstall) && (
                  <div className="bg-blue-50 rounded-lg p-4 space-y-3">
                    {/* Download section */}
                    {currentDownload?.active && currentDownload.appName && (
                      <div>
                        <p className="text-xs text-blue-500 mb-1 font-medium">Downloading</p>
                        <div className="flex items-center justify-between mb-1">
                          <TruncatedLabel text={currentDownload.appName} className="text-sm text-blue-700 font-medium pr-2" />
                          {currentDownload.downloadRateBps > 0 && (
                            <span className="text-xs text-blue-500 flex-shrink-0">
                              {currentDownload.downloadRateBps >= 1024 * 1024
                                ? `${(currentDownload.downloadRateBps / (1024 * 1024)).toFixed(1)} MB/s`
                                : currentDownload.downloadRateBps >= 1024
                                ? `${(currentDownload.downloadRateBps / 1024).toFixed(1)} KB/s`
                                : `${Math.round(currentDownload.downloadRateBps)} B/s`}
                            </span>
                          )}
                        </div>
                        {currentDownload.bytesTotal > 0 && (
                          <>
                            <div className="w-full h-1.5 bg-blue-200 rounded-full overflow-hidden">
                              <div
                                className="h-full bg-blue-500 rounded-full transition-all duration-500"
                                style={{ width: `${Math.min(100, (currentDownload.bytesDownloaded / currentDownload.bytesTotal) * 100)}%` }}
                              />
                            </div>
                            <div className="flex justify-between mt-1 text-xs text-blue-400">
                              <span>
                                {currentDownload.bytesDownloaded >= 1024 * 1024
                                  ? `${(currentDownload.bytesDownloaded / (1024 * 1024)).toFixed(1)} MB`
                                  : `${(currentDownload.bytesDownloaded / 1024).toFixed(0)} KB`}
                                {" / "}
                                {currentDownload.bytesTotal >= 1024 * 1024
                                  ? `${(currentDownload.bytesTotal / (1024 * 1024)).toFixed(1)} MB`
                                  : `${(currentDownload.bytesTotal / 1024).toFixed(0)} KB`}
                              </span>
                              <span>{Math.round((currentDownload.bytesDownloaded / currentDownload.bytesTotal) * 100)}%</span>
                            </div>
                          </>
                        )}
                      </div>
                    )}

                    {/* Install section */}
                    {currentInstall?.active && currentInstall.appName && (
                      <div>
                        <p className="text-xs text-blue-500 mb-1 font-medium">Installing</p>
                        <div className="flex items-center justify-between">
                          <div className="flex items-center space-x-1.5 min-w-0">
                            <div className="w-1.5 h-1.5 bg-blue-500 rounded-full animate-pulse flex-shrink-0" />
                            <TruncatedLabel text={currentInstall.appName} className="text-sm text-blue-700 font-medium" />
                          </div>
                          {installElapsedMs != null && installElapsedMs > 0 && (
                            <span className="text-xs text-blue-600 font-medium tabular-nums flex-shrink-0 ml-2">
                              {formatDuration(installElapsedMs)}
                            </span>
                          )}
                        </div>
                      </div>
                    )}

                    {/* App counter — always visible in app phases */}
                    {(() => {
                      const total = appSummary?.total ?? currentInstall?.totalCount ?? 0;
                      const installed = appSummary?.installed ?? currentInstall?.completedCount ?? 0;
                      const failed = appSummary?.failed ?? currentInstall?.failedCount ?? 0;
                      if (total === 0) return null;
                      return (
                        <div className="flex items-center justify-between text-xs text-blue-600 pt-1 border-t border-blue-100">
                          <span className="font-medium">
                            {installed}{failed > 0 ? ` + ${failed} failed` : ""} / {total} apps installed
                          </span>
                        </div>
                      );
                    })()}
                  </div>
                )}

                {session.status === "Succeeded" && (
                  <div className="bg-green-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-green-700 font-medium">
                      Your device is ready to use! Total setup time:{" "}
                      {Math.round(session.durationSeconds / 60)} minutes.
                    </p>
                    <p className="text-xs text-green-600 mt-1">
                      Completed at{" "}
                      {new Date(
                        new Date(session.startedAt).getTime() + session.durationSeconds * 1000
                      ).toLocaleString(undefined, {
                        dateStyle: "medium",
                        timeStyle: "short",
                      })}
                    </p>
                  </div>
                )}

                {session.status === "Failed" && (
                  <div className="bg-red-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-red-700">
                      {session.failureReason ||
                        "Setup encountered an error. Please contact your IT department."}
                    </p>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </div>
    </ProtectedRoute>
  );
}
