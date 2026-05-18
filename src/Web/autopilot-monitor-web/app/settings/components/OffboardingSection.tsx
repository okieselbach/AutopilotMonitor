"use client";

import { useEffect, useRef, useState } from "react";

interface OffboardingInProgressInfo {
  status: string;                     // "Queued" | "Initiated" | "InProgress" | "Completed" | "Failed"
  historyRowKey: string;
  earliestProcessingAt?: string | null; // ISO UTC; drives countdown in drain-barrier state
  message: string;
}

interface OffboardingSectionProps {
  showOffboardDialog: boolean;
  setShowOffboardDialog: (value: boolean) => void;
  offboardConfirmText: string;
  setOffboardConfirmText: (value: string) => void;
  offboarding: boolean;
  offboardError: string | null;
  setOffboardError: (value: string | null) => void;
  onOffboard: () => void;

  /** Set after the DELETE returns 202; switches the section into the drain-barrier banner state. */
  offboardingInProgress: OffboardingInProgressInfo | null;

  /** Invoked by the banner's auto-logout timer once the drain barrier has fully elapsed. */
  onDrainBarrierElapsed: () => void;
}

function formatRemaining(totalSeconds: number): string {
  if (totalSeconds <= 0) return "0 min";
  return `~${Math.ceil(totalSeconds / 60)} min`;
}

/** Live countdown — re-renders every second until EarliestProcessingAt passes, then calls onElapsed once. */
function useCountdown(targetIso: string | null | undefined, onElapsed: () => void): number {
  const [secondsLeft, setSecondsLeft] = useState<number>(() => {
    if (!targetIso) return 0;
    return Math.max(0, Math.floor((new Date(targetIso).getTime() - Date.now()) / 1000));
  });
  const firedRef = useRef(false);

  useEffect(() => {
    if (!targetIso) return;
    firedRef.current = false;

    // Anchor the remaining-time once at mount, then drive subsequent ticks off
    // performance.now() — a monotonic clock that is NOT affected by NTP-syncs
    // or manual clock changes during the ~6 min drain barrier. The previous
    // implementation re-read Date.now() on every tick, so a wall-clock jump
    // forward would fire onElapsed (auto-logout) prematurely.
    const initialRemainingMs = Math.max(
      0,
      new Date(targetIso).getTime() - Date.now(),
    );
    const monoAtMount = performance.now();

    const tick = () => {
      const elapsedMs = performance.now() - monoAtMount;
      const remaining = Math.max(0, Math.floor((initialRemainingMs - elapsedMs) / 1000));
      setSecondsLeft(remaining);
      if (remaining === 0 && !firedRef.current) {
        firedRef.current = true;
        onElapsed();
      }
    };

    tick(); // immediate sync
    const interval = setInterval(tick, 1000);
    return () => clearInterval(interval);
  }, [targetIso, onElapsed]);

  return secondsLeft;
}

export default function OffboardingSection({
  showOffboardDialog,
  setShowOffboardDialog,
  offboardConfirmText,
  setOffboardConfirmText,
  offboarding,
  offboardError,
  setOffboardError,
  onOffboard,
  offboardingInProgress,
  onDrainBarrierElapsed,
}: OffboardingSectionProps) {
  // When the offboarding has been queued, replace the section entirely with the
  // drain-barrier banner. The dialog is dismissed by handleOffboard on success.
  if (offboardingInProgress) {
    return (
      <OffboardingInProgressBanner
        info={offboardingInProgress}
        onDrainBarrierElapsed={onDrainBarrierElapsed}
      />
    );
  }

  return (
    <>
      {/* Danger Zone: Tenant Offboarding */}
      <div className="bg-white rounded-lg shadow border-2 border-red-200">
        <div className="p-6 border-b border-red-100 bg-red-50">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-red-900">Danger Zone</h2>
              <p className="text-sm text-red-600 mt-1">Irreversible and destructive actions</p>
            </div>
          </div>
        </div>
        <div className="p-6">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-base font-semibold text-gray-900">Offboard this Tenant</h3>
              <p className="text-sm text-gray-500 mt-1">
                Permanently deletes <strong>all data</strong> for this tenant – sessions, events, rules, audit logs, configuration, and all admin accounts including yours. This cannot be undone.
              </p>
            </div>
            <button
              onClick={() => { setShowOffboardDialog(true); setOffboardConfirmText(""); setOffboardError(null); }}
              className="ml-6 flex-shrink-0 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors text-sm font-medium"
            >
              Offboard Tenant
            </button>
          </div>
        </div>
      </div>

      {/* Offboard Confirmation Dialog */}
      {showOffboardDialog && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
            <div className="flex items-center space-x-3 mb-4">
              <div className="w-12 h-12 bg-red-100 rounded-full flex items-center justify-center flex-shrink-0">
                <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                </svg>
              </div>
              <div>
                <h3 className="text-lg font-bold text-gray-900">Tenant Offboarding</h3>
                <p className="text-sm text-red-600 font-medium">This action is permanent and cannot be undone.</p>
              </div>
            </div>

            <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4 text-sm text-red-800 space-y-1">
              <p className="font-semibold">The following will be permanently deleted:</p>
              <ul className="list-disc list-inside mt-2 space-y-1">
                <li>All enrollment sessions and events</li>
                <li>All gather and analyze rules</li>
                <li>Audit logs and usage metrics</li>
                <li>Tenant configuration</li>
                <li>All admin accounts (including yours)</li>
              </ul>
              <p className="mt-3 font-semibold">
                Data deletion will start after a ~6 minute preparation window and then run in the background.
                You can close this tab once the request has been sent.
              </p>
            </div>

            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Type <span className="font-bold text-red-600">OFFBOARD</span> to confirm
              </label>
              <input
                type="text"
                value={offboardConfirmText}
                onChange={(e) => setOffboardConfirmText(e.target.value)}
                placeholder="OFFBOARD"
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                autoComplete="off"
              />
            </div>

            {offboardError && (
              <div className="mb-4 bg-red-50 border border-red-200 rounded p-3 text-sm text-red-800">
                {offboardError}
              </div>
            )}

            <div className="flex space-x-3">
              <button
                onClick={() => { setShowOffboardDialog(false); setOffboardConfirmText(""); setOffboardError(null); }}
                disabled={offboarding}
                className="flex-1 px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={onOffboard}
                disabled={offboarding || offboardConfirmText !== "OFFBOARD"}
                className="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center justify-center space-x-2"
              >
                {offboarding ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Queuing...</span>
                  </>
                ) : (
                  <span>Queue Permanent Deletion</span>
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/**
 * Banner shown after the DELETE returns 202. Replaces the entire Danger Zone section.
 * Renders a live countdown until `earliestProcessingAt`, then auto-logs the user out
 * (the worker has by then started Phase 2 and the auth pipeline will return 403
 * anyway via the Disabled-flag gate).
 */
function OffboardingInProgressBanner({
  info,
  onDrainBarrierElapsed,
}: {
  info: OffboardingInProgressInfo;
  onDrainBarrierElapsed: () => void;
}) {
  const secondsLeft = useCountdown(info.earliestProcessingAt ?? null, onDrainBarrierElapsed);
  const inDrainBarrier = (info.earliestProcessingAt && secondsLeft > 0) === true;

  // Single 4-state visual; status text drives the badge color + label.
  // "Preparing" is derived from earliestProcessingAt + countdown rather than from status,
  // because the backend status stays "Queued" during the cache-drain window.
  const stateLabel = inDrainBarrier
    ? "Preparing"
    : info.status === "Failed"
      ? "Failed"
      : info.status === "Completed"
        ? "Completed"
        : "In Progress";

  const stateColors: Record<string, string> = {
    "Preparing": "bg-amber-100 text-amber-800 border-amber-300",
    "In Progress": "bg-blue-100 text-blue-800 border-blue-300",
    "Completed": "bg-green-100 text-green-800 border-green-300",
    "Failed": "bg-red-100 text-red-800 border-red-300",
  };

  return (
    <div className="bg-white rounded-lg shadow border-2 border-red-200 p-6 space-y-4">
      <div className="flex items-center space-x-3">
        <div className="w-12 h-12 bg-red-100 rounded-full flex items-center justify-center flex-shrink-0">
          <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
          </svg>
        </div>
        <div>
          <h2 className="text-xl font-semibold text-red-900">Tenant Offboarding queued</h2>
          <p className="text-sm text-gray-600 mt-1">
            This tenant will be permanently deleted. You do not need to keep this page open.
          </p>
        </div>
      </div>

      <div className="flex items-center space-x-3">
        <span className={`inline-flex items-center px-3 py-1 rounded-full text-sm font-medium border ${stateColors[stateLabel] ?? stateColors["In Progress"]}`}>
          {stateLabel}
        </span>
        {inDrainBarrier && (
          <span className="text-sm text-gray-700">
            Data deletion starts in <span className="font-mono font-semibold">{formatRemaining(secondsLeft)}</span>
          </span>
        )}
      </div>

      {inDrainBarrier ? (
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-900 space-y-1">
          <p className="font-semibold">Preparing for deletion</p>
          <p>
            Your tenant has been disabled and is being prepared for permanent deletion. The wipe will start
            automatically once the countdown reaches zero.
          </p>
          <p className="mt-2">
            Everything needed for the deletion is already recorded — you can sign out and close this tab.
            The deletion continues in the background.
          </p>
        </div>
      ) : (
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 text-sm text-blue-900 space-y-1">
          <p className="font-semibold">Deletion in progress</p>
          <p>
            Your tenant is being permanently deleted. You can close this tab safely.
          </p>
        </div>
      )}

      <div className="text-xs text-gray-500 space-y-0.5">
        <p>History row: <span className="font-mono">{info.historyRowKey}</span></p>
        {info.earliestProcessingAt && (
          <p>Earliest processing at: <span className="font-mono">{info.earliestProcessingAt}</span></p>
        )}
      </div>
    </div>
  );
}
