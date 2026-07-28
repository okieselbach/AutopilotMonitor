// Pure state helpers for the session-detail "Collect Logs" button. No React/JSX so
// vitest can pin the role/config gating matrix and the progress evaluation without
// rendering (same pattern as diagnosticsSasExpiry.ts).

export type CollectPhase = "idle" | "working" | "queued" | "collecting";

/** Session statuses in which the agent is (potentially) still alive and can receive actions. */
export const ACTIVE_SESSION_STATUSES = ["InProgress", "Pending", "Stalled"];

/** Give up waiting for agent feedback after this long; the package still appears later if the agent reconnects. */
export const COLLECT_TIMEOUT_MS = 3 * 60_000;

export interface CollectButtonInput {
  sessionStatus?: string | null;
  /** Cross-tenant (fleet drill-in) views cannot queue actions: the backend resolves the tenant strictly from the caller's JWT. */
  isCrossTenantView: boolean;
  isTenantAdmin: boolean;
  isOperator: boolean;
  diagnosticsConfigured: boolean;
  phase: CollectPhase;
}

export interface CollectButtonState {
  enabled: boolean;
  /** When true, the click opens the quick-config dialog instead of queueing directly (Admin on an unconfigured tenant). */
  opensQuickConfig: boolean;
  /** Tooltip for the disabled states — the button stays visible so users learn the capability exists. */
  tooltip?: string;
  /** Progress label replacing the idle caption while a collection is running. */
  busyLabel?: string;
}

export function resolveCollectButtonState(input: CollectButtonInput): CollectButtonState {
  if (input.phase !== "idle") {
    return {
      enabled: false,
      opensQuickConfig: false,
      busyLabel:
        input.phase === "collecting" ? "Collecting…"
        : input.phase === "queued" ? "Waiting for agent…"
        : "Requesting…",
    };
  }

  const isActive = !!input.sessionStatus && ACTIVE_SESSION_STATUSES.includes(input.sessionStatus);
  if (!isActive) {
    return {
      enabled: false,
      opensQuickConfig: false,
      tooltip: "The agent is no longer active for this session — logs can only be collected while an enrollment is running.",
    };
  }

  if (input.isCrossTenantView) {
    return {
      enabled: false,
      opensQuickConfig: false,
      tooltip: "Not available in the cross-tenant view.",
    };
  }

  if (!input.diagnosticsConfigured) {
    if (input.isTenantAdmin) {
      // Admin path: the click opens the quick-config dialog (enable hosted upload, then collect).
      return { enabled: true, opensQuickConfig: true };
    }
    return {
      enabled: false,
      opensQuickConfig: false,
      tooltip: "Diagnostics upload is not configured for this tenant — a Tenant Admin can enable it in Settings.",
    };
  }

  if (input.isTenantAdmin || input.isOperator) {
    return { enabled: true, opensQuickConfig: false };
  }

  return {
    enabled: false,
    opensQuickConfig: false,
    tooltip: "Requires the Operator or Admin role.",
  };
}

// ── Progress evaluation ────────────────────────────────────────────────────────────

/** Minimal event shape needed for progress tracking (subset of EnrollmentEvent). */
export interface CollectWatchEvent {
  eventType: string;
  sequence: number;
  data?: Record<string, unknown>;
}

export interface CollectWatchInput {
  phase: CollectPhase;
  /** Highest event sequence at the moment the collection was requested — only newer events count. */
  baselineSequence: number;
  /** DiagnosticsBlobName at request time; ANY change means a fresh package landed. */
  baselineBlobName?: string;
  currentBlobName?: string;
  events: CollectWatchEvent[];
}

export type CollectWatchResult =
  | { kind: "none" }
  | { kind: "collecting" }
  | { kind: "done"; blobName: string }
  | { kind: "failed"; reason: string };

/**
 * Evaluates the live event stream + session delta for progress on a pending collection.
 * Sequence-based (backend-assigned, monotonic) rather than timestamp-based so agent
 * clock skew cannot hide or resurrect matches.
 */
export function evaluateCollectProgress(input: CollectWatchInput): CollectWatchResult {
  if (input.phase !== "queued" && input.phase !== "collecting") return { kind: "none" };

  // Ultimate goal first: the Sessions row carries a new package name (delivered via the
  // SignalR sessionUpdate delta) — the Download button is live regardless of which
  // server_action events made it into the visible stream.
  if (input.currentBlobName && input.currentBlobName !== input.baselineBlobName) {
    return { kind: "done", blobName: input.currentBlobName };
  }

  const relevant = input.events.filter(
    (e) => (e.sequence ?? 0) > input.baselineSequence
      && e.data?.["actionType"] === "request_diagnostics",
  );

  const failed = relevant.find((e) => e.eventType === "server_action_failed");
  if (failed) {
    const reason = typeof failed.data?.["failureReason"] === "string" && failed.data["failureReason"]
      ? (failed.data["failureReason"] as string)
      : "diagnostics upload failed";
    return { kind: "failed", reason };
  }

  const started = relevant.some(
    (e) => e.eventType === "server_action_received" || e.eventType === "server_action_executed",
  );
  if (started) return { kind: "collecting" };

  return { kind: "none" };
}
