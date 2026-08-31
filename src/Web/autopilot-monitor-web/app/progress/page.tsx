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
import { DeviceStatusChips } from "./components/DeviceStatusChips";
import type { PresentationKind } from "./hooks/progressLayout";

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

// Presentation kind -> card styling. Only "failed" is red: a waiting pre-provisioned device,
// a stalled device or an Incomplete verdict must not read as a failure to the end user.
const PRESENTATION_STYLES: Record<
  PresentationKind,
  { header: string; title: string; bar: string; percent: string }
> = {
  working: { header: "bg-blue-50 border-b border-blue-100", title: "text-blue-800", bar: "bg-blue-500", percent: "text-blue-600" },
  waiting: { header: "bg-sky-50 border-b border-sky-100", title: "text-sky-800", bar: "bg-sky-500", percent: "text-sky-600" },
  success: { header: "bg-green-50 border-b border-green-100", title: "text-green-800", bar: "bg-green-500", percent: "text-green-600" },
  failed: { header: "bg-red-50 border-b border-red-100", title: "text-red-800", bar: "bg-red-500", percent: "text-red-600" },
  incomplete: { header: "bg-gray-50 border-b border-gray-200", title: "text-gray-800", bar: "bg-gray-400", percent: "text-gray-600" },
};

type StepState = "completed" | "current" | "failed" | "pending";

function stepState(index: number, activeStepIndex: number, kind: PresentationKind): StepState {
  if (index < activeStepIndex) return "completed";
  if (index > activeStepIndex) return "pending";
  if (kind === "failed") return "failed";
  if (kind === "working" || kind === "waiting") return "current";
  return "pending";
}

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
    addNotification,
  });

  const {
    appSummary,
    currentDownload,
    currentInstall,
    installElapsedMs,
    overallProgress,
    deviceStatus,
    steps,
    activeStepIndex,
    presentation,
    scenario,
  } = useProgressDerivedData(events, session);

  const kind: PresentationKind = presentation?.kind ?? "working";
  const styles = PRESENTATION_STYLES[kind];
  const activeStep = steps[activeStepIndex] ?? null;
  // Live app panel: the device is on an app step and still working on it.
  const showAppPanel =
    kind === "working" &&
    activeStep?.isAppsStep === true &&
    (appSummary !== null || currentDownload !== null || currentInstall !== null);

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
                  className="w-4 h-4 text-green-600 flex-shrink-0"
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
                <span className="text-xs text-green-700 font-medium">Change device</span>
                <svg
                  className="w-3.5 h-3.5 text-green-700"
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
                <div className="inline-flex items-center justify-center w-16 h-16 bg-green-100 rounded-full mb-4">
                  <svg
                    className="w-8 h-8 text-green-600"
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
                  Check the setup status of your device
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
                    placeholder="Serial number or device name..."
                    title="Windows 365 Cloud PC: enter the device name shown in the Windows App."
                    className="w-full px-4 py-3 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 text-lg"
                  />
                </div>
                <button
                  onClick={searchBySerial}
                  disabled={searching || !serialInput.trim()}
                  className="px-6 py-3 bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed font-medium whitespace-nowrap"
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
          {session && presentation && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
              {/* Status Header */}
              <div className={`px-6 py-4 ${styles.header}`}>
                <div className="text-center">
                  <h2 className={`text-xl font-semibold ${styles.title}`}>{presentation.title}</h2>
                  <p className="text-sm text-gray-500 mt-1">
                    {session.deviceName || session.serialNumber} |{" "}
                    {session.manufacturer} {session.model}
                  </p>
                  {scenario && (
                    <span className="inline-block mt-1.5 px-2 py-0.5 text-xs text-gray-600 border border-gray-300 rounded-full">
                      {scenario}
                    </span>
                  )}
                  {presentation.detail && (
                    <p className="text-sm text-gray-600 mt-2">{presentation.detail}</p>
                  )}
                  {/* Live chips only while working — on finished or parked sessions the
                      last-known values would masquerade as current state. */}
                  {kind === "working" && <DeviceStatusChips status={deviceStatus} />}
                </div>
              </div>

              <div className="p-6">
                {/* Overall Progress Bar */}
                <div className="mb-8">
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm text-gray-500">
                      Overall Progress
                    </span>
                    <span className={`text-sm font-semibold ${styles.percent}`}>
                      {Math.round(overallProgress)}%
                    </span>
                  </div>
                  <div className="w-full h-4 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all duration-1000 ${styles.bar}`}
                      style={{ width: `${overallProgress}%` }}
                    />
                  </div>
                </div>

                {/* Phase Steps */}
                <div className="space-y-3 mb-8">
                  {steps.map((step, index) => {
                    const state = stepState(index, activeStepIndex, kind);
                    const isCompleted = state === "completed";
                    const isCurrent = state === "current";
                    const isFailed = state === "failed";
                    const showActivity = isCurrent && kind === "working" && step.isAppsStep;

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
                              {showActivity &&
                                appSummary &&
                                appSummary.total > 0 &&
                                ` (${appSummary.installed}/${appSummary.total})`}
                            </span>
                            {/* Current activity detail below the active step */}
                            {showActivity && (
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

                {/* Activity Details — visible while the device works on an app step */}
                {showAppPanel && (
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

                {kind === "success" && (
                  <div className="bg-green-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-green-700 font-medium">
                      Your device is ready to use! Total setup time:{" "}
                      {Math.round((session.durationSeconds ?? 0) / 60)} minutes.
                    </p>
                    <p className="text-xs text-green-600 mt-1">
                      Completed at{" "}
                      {new Date(
                        new Date(session.startedAt).getTime() + (session.durationSeconds ?? 0) * 1000
                      ).toLocaleString(undefined, {
                        dateStyle: "medium",
                        timeStyle: "short",
                      })}
                    </p>
                  </div>
                )}

                {kind === "failed" && (
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
