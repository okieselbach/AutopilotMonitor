import { describe, it, expect } from "vitest";
import {
  evaluateCollectProgress,
  resolveCollectButtonState,
  ACTIVE_SESSION_STATUSES,
  CollectWatchEvent,
} from "../collectLogsLogic";

// The Collect Logs button is ALWAYS rendered; these tests pin which combination of
// session state / role / tenant configuration enables it, when a click opens the
// quick-config dialog instead, and how live events + the DiagnosticsBlobName delta
// drive the progress state. Pure logic — no DOM.

const baseInput = {
  sessionStatus: "InProgress",
  isCrossTenantView: false,
  isTenantAdmin: false,
  isOperator: false,
  diagnosticsConfigured: true,
  phase: "idle" as const,
};

describe("resolveCollectButtonState", () => {
  it("enables direct collect for an Operator when diagnostics is configured", () => {
    const s = resolveCollectButtonState({ ...baseInput, isOperator: true });
    expect(s.enabled).toBe(true);
    expect(s.opensQuickConfig).toBe(false);
  });

  it("enables direct collect for a Tenant Admin when diagnostics is configured", () => {
    const s = resolveCollectButtonState({ ...baseInput, isTenantAdmin: true });
    expect(s.enabled).toBe(true);
    expect(s.opensQuickConfig).toBe(false);
  });

  it("routes an Admin on an UNCONFIGURED tenant into the quick-config dialog", () => {
    const s = resolveCollectButtonState({
      ...baseInput, isTenantAdmin: true, diagnosticsConfigured: false,
    });
    expect(s.enabled).toBe(true);
    expect(s.opensQuickConfig).toBe(true);
  });

  it("disables (with the nudge tooltip) for an Operator on an unconfigured tenant", () => {
    const s = resolveCollectButtonState({
      ...baseInput, isOperator: true, diagnosticsConfigured: false,
    });
    expect(s.enabled).toBe(false);
    expect(s.tooltip).toMatch(/Tenant Admin can enable/);
  });

  it("disables for a Viewer even when configured", () => {
    const s = resolveCollectButtonState({ ...baseInput });
    expect(s.enabled).toBe(false);
    expect(s.tooltip).toMatch(/Operator or Admin/);
  });

  it.each(["Succeeded", "Failed", "Incomplete", undefined, null])(
    "disables for non-active session status %s (agent gone)",
    (status) => {
      const s = resolveCollectButtonState({
        ...baseInput, isTenantAdmin: true, sessionStatus: status as string | null | undefined,
      });
      expect(s.enabled).toBe(false);
      expect(s.tooltip).toMatch(/no longer active/);
    },
  );

  it.each(ACTIVE_SESSION_STATUSES)("treats %s as an active status", (status) => {
    const s = resolveCollectButtonState({ ...baseInput, isTenantAdmin: true, sessionStatus: status });
    expect(s.enabled).toBe(true);
  });

  it("disables in cross-tenant views (actions endpoint is JWT-tenant-bound)", () => {
    const s = resolveCollectButtonState({ ...baseInput, isTenantAdmin: true, isCrossTenantView: true });
    expect(s.enabled).toBe(false);
    expect(s.tooltip).toMatch(/cross-tenant/);
  });

  it("shows a busy label and disables while a collection is pending", () => {
    const queued = resolveCollectButtonState({ ...baseInput, isTenantAdmin: true, phase: "queued" });
    expect(queued.enabled).toBe(false);
    expect(queued.busyLabel).toMatch(/agent/i);

    const collecting = resolveCollectButtonState({ ...baseInput, isTenantAdmin: true, phase: "collecting" });
    expect(collecting.busyLabel).toMatch(/Collecting/);
  });
});

describe("evaluateCollectProgress", () => {
  const ev = (eventType: string, sequence: number, data?: Record<string, unknown>): CollectWatchEvent =>
    ({ eventType, sequence, data });

  const baseWatch = {
    phase: "queued" as const,
    baselineSequence: 100,
    baselineBlobName: undefined as string | undefined,
    currentBlobName: undefined as string | undefined,
    events: [] as CollectWatchEvent[],
  };

  it("is inert while idle", () => {
    expect(evaluateCollectProgress({ ...baseWatch, phase: "idle" }).kind).toBe("none");
  });

  it("reports done when the session's blob name changes — even without visible events", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      currentBlobName: "t/AgentDiagnostics-s-20260724T100000-server-requested.zip",
    });
    expect(r).toEqual({ kind: "done", blobName: "t/AgentDiagnostics-s-20260724T100000-server-requested.zip" });
  });

  it("reports done when an EXISTING blob name is replaced by a new one", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      baselineBlobName: "t/old.zip",
      currentBlobName: "t/new-server-requested.zip",
    });
    expect(r.kind).toBe("done");
  });

  it("does NOT report done while the blob name is unchanged from the baseline", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      baselineBlobName: "t/old.zip",
      currentBlobName: "t/old.zip",
    });
    expect(r.kind).toBe("none");
  });

  it("moves to collecting on server_action_received for request_diagnostics after the baseline", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      events: [ev("server_action_received", 101, { actionType: "request_diagnostics" })],
    });
    expect(r.kind).toBe("collecting");
  });

  it("ignores server_action events at or before the baseline (at-least-once redelivery)", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      events: [
        ev("server_action_received", 100, { actionType: "request_diagnostics" }),
        ev("server_action_failed", 99, { actionType: "request_diagnostics", failureReason: "old" }),
      ],
    });
    expect(r.kind).toBe("none");
  });

  it("ignores server_action events for OTHER action types", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      events: [ev("server_action_received", 101, { actionType: "rotate_config" })],
    });
    expect(r.kind).toBe("none");
  });

  it("reports failed with the agent's failureReason", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      events: [ev("server_action_failed", 102, { actionType: "request_diagnostics", failureReason: "url_host_rejected" })],
    });
    expect(r).toEqual({ kind: "failed", reason: "url_host_rejected" });
  });

  it("falls back to a generic reason when failureReason is missing", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      events: [ev("server_action_failed", 102, { actionType: "request_diagnostics" })],
    });
    expect(r.kind).toBe("failed");
  });

  it("prefers done over a stale failure when the blob eventually landed", () => {
    const r = evaluateCollectProgress({
      ...baseWatch,
      currentBlobName: "t/new.zip",
      events: [ev("server_action_failed", 102, { actionType: "request_diagnostics", failureReason: "transient" })],
    });
    expect(r.kind).toBe("done");
  });
});
