"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { EnrollmentEvent } from "@/types";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { NotificationType } from "@/contexts/NotificationContext";
import {
  CollectPhase,
  COLLECT_TIMEOUT_MS,
  evaluateCollectProgress,
  resolveCollectButtonState,
} from "./collectLogsLogic";

interface CollectLogsButtonProps {
  sessionId: string;
  effectiveTenantId?: string;
  sessionStatus?: string;
  /** Live from the session object (SignalR sessionUpdate delta) — a change means the package landed. */
  diagnosticsBlobName?: string;
  isCrossTenantView: boolean;
  isTenantAdmin: boolean;
  isOperator: boolean;
  diagnosticsConfigured: boolean;
  /** Called after a successful quick-config so the page-level flag flips without a refetch. */
  onDiagnosticsConfigured: () => void;
  events: EnrollmentEvent[];
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  addNotification: (type: NotificationType, title: string, message: string, key?: string) => void;
}

/**
 * "Collect Logs" — queues a `request_diagnostics` ServerAction that the agent picks up with
 * its next ingest response, then tracks progress via the live event stream
 * (server_action_received/_executed/_failed) and the DiagnosticsBlobName session delta.
 *
 * Always rendered, even when disabled: the visible-but-disabled button (with a tooltip
 * explaining what is missing) is the deliberate nudge towards configuring diagnostics upload.
 * Tenant Admins on an unconfigured tenant get the quick-config dialog instead (enable
 * Hosted + OnFailure, then collect immediately) — that dialog IS the explicit opt-in.
 */
export default function CollectLogsButton({
  sessionId,
  effectiveTenantId,
  sessionStatus,
  diagnosticsBlobName,
  isCrossTenantView,
  isTenantAdmin,
  isOperator,
  diagnosticsConfigured,
  onDiagnosticsConfigured,
  events,
  getAccessToken,
  addNotification,
}: CollectLogsButtonProps) {
  const [phase, setPhase] = useState<CollectPhase>("idle");
  const [showQuickConfig, setShowQuickConfig] = useState(false);
  const [quickConfigBusy, setQuickConfigBusy] = useState(false);

  const baselineSequenceRef = useRef(0);
  const baselineBlobRef = useRef<string | undefined>(undefined);
  const timeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const buttonState = resolveCollectButtonState({
    sessionStatus,
    isCrossTenantView,
    isTenantAdmin,
    isOperator,
    diagnosticsConfigured,
    phase,
  });

  const notifyError = useCallback((err: unknown, fallback: string) => {
    if (err instanceof TokenExpiredError) {
      addNotification("error", "Session Expired", err.message, "session-expired-error");
    } else {
      addNotification("error", "Collect Logs", fallback, "collect-logs");
    }
  }, [addNotification]);

  const queueAction = useCallback(async (type: string, reason: string): Promise<boolean> => {
    const res = await authenticatedFetch(
      api.sessions.queueAction(sessionId, effectiveTenantId),
      getAccessToken,
      {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ type, reason }),
      },
    );
    return res.ok;
  }, [sessionId, effectiveTenantId, getAccessToken]);

  const startCollect = useCallback(async () => {
    setPhase("working");
    // Sequence baseline: only server_action events NEWER than the click count as feedback
    // for THIS request (at-least-once redelivery of older actions must not match).
    baselineSequenceRef.current = events.reduce((max, e) => Math.max(max, e.sequence ?? 0), 0);
    baselineBlobRef.current = diagnosticsBlobName;
    try {
      const ok = await queueAction("request_diagnostics", "On-demand log collection from portal");
      if (!ok) {
        setPhase("idle");
        addNotification("error", "Collect Logs", "Failed to queue the log collection request.", "collect-logs");
        return;
      }
      setPhase("queued");
      addNotification("info", "Collect Logs",
        "Request queued — the agent picks it up with its next check-in and uploads a diagnostics package.",
        "collect-logs");
    } catch (err) {
      setPhase("idle");
      notifyError(err, "Failed to queue the log collection request.");
    }
  }, [events, diagnosticsBlobName, queueAction, addNotification, notifyError]);

  const handleClick = () => {
    if (!buttonState.enabled) return;
    if (buttonState.opensQuickConfig) {
      setShowQuickConfig(true);
    } else {
      void startCollect();
    }
  };

  // Quick-config (Admin only): read the full tenant config, flip ONLY the two diagnostics
  // fields, write it back verbatim, then rotate_config BEFORE request_diagnostics so the
  // agent refetches the now-enabled config before building the package.
  const handleQuickConfig = async () => {
    if (!effectiveTenantId) return;
    setQuickConfigBusy(true);
    try {
      const getRes = await authenticatedFetch(api.config.tenant(effectiveTenantId), getAccessToken);
      if (!getRes.ok) throw new Error(`Failed to load tenant configuration (${getRes.status})`);
      const config = await getRes.json();

      const updated = {
        ...config,
        diagnosticsUploadDestination: "Hosted",
        // OnFailure is the least invasive mode that unlocks on-demand collection (the
        // server-requested path always uploads); keep a stricter existing mode untouched.
        diagnosticsUploadMode:
          !config.diagnosticsUploadMode || config.diagnosticsUploadMode === "Off"
            ? "OnFailure"
            : config.diagnosticsUploadMode,
      };

      const putRes = await authenticatedFetch(api.config.tenant(effectiveTenantId), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updated),
      });
      if (!putRes.ok) throw new Error(`Failed to save tenant configuration (${putRes.status})`);

      onDiagnosticsConfigured();
      setShowQuickConfig(false);

      // Order matters: the agent processes queued actions sequentially, so rotate_config
      // must be first for the collection to run against the new config.
      const rotated = await queueAction("rotate_config", "Enable diagnostics upload (quick config from portal)");
      if (!rotated) {
        addNotification("warning", "Collect Logs",
          "Configuration saved, but queueing the config refresh failed — try Collect Logs again.",
          "collect-logs");
        return;
      }
      await startCollect();
    } catch (err) {
      notifyError(err, err instanceof Error ? err.message : "Quick configuration failed.");
    } finally {
      setQuickConfigBusy(false);
    }
  };

  // Progress watcher: SignalR keeps `events` and the session delta fresh; sequence-based
  // matching (see collectLogsLogic) makes this immune to agent clock skew.
  useEffect(() => {
    const result = evaluateCollectProgress({
      phase,
      baselineSequence: baselineSequenceRef.current,
      baselineBlobName: baselineBlobRef.current,
      currentBlobName: diagnosticsBlobName,
      events,
    });

    if (result.kind === "done") {
      setPhase("idle");
      addNotification("success", "Collect Logs",
        "Diagnostics package uploaded — it is now available via Download Diagnostics.",
        "collect-logs");
    } else if (result.kind === "failed") {
      setPhase("idle");
      addNotification("error", "Collect Logs", `The agent could not upload the package: ${result.reason}`, "collect-logs");
    } else if (result.kind === "collecting" && phase === "queued") {
      setPhase("collecting");
    }
  }, [events, diagnosticsBlobName, phase, addNotification]);

  // Give up after a quiet timeout — the agent may have exited. The package still appears
  // via SignalR if it reconnects later; the button just returns to idle for a retry.
  useEffect(() => {
    if (phase === "queued" || phase === "collecting") {
      if (!timeoutRef.current) {
        timeoutRef.current = setTimeout(() => {
          timeoutRef.current = null;
          setPhase("idle");
          addNotification("warning", "Collect Logs",
            "No response from the agent yet — it may no longer be running. The package will still appear if the agent checks in later.",
            "collect-logs");
        }, COLLECT_TIMEOUT_MS);
      }
    } else if (timeoutRef.current) {
      clearTimeout(timeoutRef.current);
      timeoutRef.current = null;
    }
    return () => {
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
        timeoutRef.current = null;
      }
    };
  }, [phase, addNotification]);

  return (
    <>
      <button
        onClick={handleClick}
        disabled={!buttonState.enabled}
        title={buttonState.tooltip}
        className={`px-4 py-2 rounded-md transition-colors flex items-center gap-2 text-sm ${
          buttonState.enabled
            ? "bg-sky-600 text-white hover:bg-sky-700"
            : "bg-white border border-gray-200 text-gray-400 cursor-not-allowed"
        }`}
      >
        {phase !== "idle" ? (
          <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
          </svg>
        ) : (
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M9 17v-2a2 2 0 012-2h2a2 2 0 012 2v2m-8 4h10a2 2 0 002-2V9a2 2 0 00-2-2h-3.586a1 1 0 01-.707-.293l-1.414-1.414A1 1 0 0012.586 5H7a2 2 0 00-2 2v12a2 2 0 002 2z" />
          </svg>
        )}
        {buttonState.busyLabel ?? "Collect Logs"}
      </button>

      {showQuickConfig && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={() => !quickConfigBusy && setShowQuickConfig(false)}>
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
            <div className="p-6">
              <div className="flex items-center mb-4">
                <div className="flex-shrink-0 w-12 h-12 bg-sky-100 rounded-full flex items-center justify-center">
                  <svg className="w-6 h-6 text-sky-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
                      d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  </svg>
                </div>
                <h3 className="ml-4 text-lg font-semibold text-gray-900">Enable Diagnostics Upload</h3>
              </div>
              <div className="mb-6 space-y-2">
                <p className="text-sm text-gray-700">
                  Diagnostics upload is not configured for this tenant, so the agent has nowhere to send the logs.
                </p>
                <p className="text-sm text-gray-700">
                  <span className="font-semibold">Enable &amp; Collect</span> switches the upload destination to
                  {" "}<span className="font-semibold">hosted storage</span> (managed by AutopilotMonitor) with mode
                  {" "}<span className="font-semibold">On failure</span>, then collects the logs from this device right away.
                </p>
                <p className="text-sm text-gray-600">
                  You can change the destination (e.g. to your own Azure Blob Storage) or turn uploads off again at any
                  time under Settings → Diagnostics Package.
                </p>
              </div>
              <div className="flex justify-end gap-3">
                <button
                  onClick={() => setShowQuickConfig(false)}
                  disabled={quickConfigBusy}
                  className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors disabled:opacity-50"
                >
                  Cancel
                </button>
                <button
                  onClick={() => void handleQuickConfig()}
                  disabled={quickConfigBusy}
                  className="px-4 py-2 bg-sky-600 text-white rounded-md hover:bg-sky-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                >
                  {quickConfigBusy && (
                    <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
                      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
                    </svg>
                  )}
                  Enable &amp; Collect
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
