import { describe, it, expect } from "vitest";
import {
  classifyDeleteResponse,
  classifyPollingResponse,
  dispatchSessionDeleted,
} from "@/app/dashboard/hooks/deleteSessionResponse";

/**
 * Pure-logic tests for the response classifier extracted from `useDeleteSession`. The backend
 * only emits 202 (cascade enqueued), 404, 409, 503, or 5xx; these tests pin the per-status
 * branches so the UX hint string the backend sends never silently changes shape on the frontend.
 *
 * No React rendering — the hook composes these helpers with `useSignalR` for the side-effects
 * (toast, joinGroup, row removal); the helpers are the testable contract.
 */

const SESSION_ID = "22222222-2222-2222-2222-222222222222";
const TENANT_ID  = "11111111-1111-1111-1111-111111111111";

function jsonResponse(status: number, body: object): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function emptyResponse(status: number): Response {
  // Body is empty — the helper must handle malformed/missing JSON without throwing.
  return new Response("", { status });
}

describe("classifyDeleteResponse", () => {
  it("classifies 202 as queued with manifestId", async () => {
    const r = jsonResponse(202, {
      success: true,
      status: "queued",
      manifestId: "01J0ABCDEFG",
      message: "Cascade deletion queued; worker will drain asynchronously.",
    });

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action).toEqual({
      kind: "queued",
      sessionId: SESSION_ID,
      tenantId: TENANT_ID,
      manifestId: "01J0ABCDEFG",
    });
  });

  it("classifies 409 already-in-flight with the right title", async () => {
    const r = jsonResponse(409, {
      success: false,
      message: "A cascade for this session is already in flight.",
      deletionState: "Running",
      manifestId: "01J0ABCDEFG",
      hint: "cascade_already_in_flight",
    });

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action.kind).toBe("conflict");
    if (action.kind !== "conflict") throw new Error("type narrowing");
    expect(action.title).toBe("Cascade already in flight");
    expect(action.code).toBe("cascade_already_in_flight");
    expect(action.message).toContain("already in flight");
  });

  it("classifies 409 poisoned with the restore-hint title", async () => {
    const r = jsonResponse(409, {
      success: false,
      message: "Cascade is poisoned; recover via POST /api/global/sessions/{id}/restore before retrying delete.",
      deletionState: "Poisoned",
      hint: "cascade_poisoned_use_restore",
    });

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action.kind).toBe("conflict");
    if (action.kind !== "conflict") throw new Error("type narrowing");
    expect(action.title).toBe("Cascade poisoned");
    expect(action.code).toBe("cascade_poisoned_use_restore");
    // The UI surfaces this verbatim → must mention the restore endpoint so the user knows where to go.
    expect(action.message).toContain("/restore");
  });

  it("classifies 503 kill-switch as unavailable", async () => {
    const r = jsonResponse(503, {
      success: false,
      message: "Session deletion is temporarily disabled by global kill-switch.",
      hint: "kill_switch_active",
    });

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action.kind).toBe("unavailable");
    if (action.kind !== "unavailable") throw new Error("type narrowing");
    expect(action.message).toContain("kill-switch");
  });

  it("reads the error envelope (error + PascalCase code) and still maps the legacy hint", async () => {
    const r = jsonResponse(409, {
      error: "Cascade is poisoned; recover via POST /api/global/sessions/{id}/restore before retrying delete.",
      code: "CascadePoisonedUseRestore",
      correlationId: "cid-1",
      deletionState: "Poisoned",
      manifestId: "01J0123456789ABCDEFGHIJKLM",
    });
    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);
    expect(action.kind).toBe("conflict");
    if (action.kind !== "conflict") throw new Error("unreachable");
    expect(action.title).toBe("Cascade poisoned");
    expect(action.code).toBe("CascadePoisonedUseRestore");
    expect(action.message).toContain("/restore");
  });

  it("classifies 404 as notFound (idempotent: row already gone)", async () => {
    const r = jsonResponse(404, { success: false, message: `Session ${SESSION_ID} not found` });

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action.kind).toBe("notFound");
  });

  it("classifies 500 as error with the backend message", async () => {
    const r = jsonResponse(500, { success: false, message: "Internal server error" });

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action.kind).toBe("error");
    if (action.kind !== "error") throw new Error("type narrowing");
    expect(action.message).toBe("Internal server error");
  });

  it("falls back to 'HTTP <status>' when the body is missing/malformed", async () => {
    const r = emptyResponse(502);

    const action = await classifyDeleteResponse(r, SESSION_ID, TENANT_ID);

    expect(action.kind).toBe("error");
    if (action.kind !== "error") throw new Error("type narrowing");
    expect(action.message).toBe("HTTP 502");
  });
});

describe("dispatchSessionDeleted (polling fallback + SignalR dispatch)", () => {
  it("returns the sessionId when payload matches a pending id", () => {
    const pending = new Set([SESSION_ID]);
    const result = dispatchSessionDeleted({ sessionId: SESSION_ID, tenantId: TENANT_ID }, pending);
    expect(result).toBe(SESSION_ID);
  });

  it("returns null when the sessionId is not pending (ignore stale events)", () => {
    const pending = new Set(["other-session"]);
    const result = dispatchSessionDeleted({ sessionId: SESSION_ID }, pending);
    expect(result).toBeNull();
  });

  it("returns null when the payload has no sessionId", () => {
    const pending = new Set([SESSION_ID]);
    expect(dispatchSessionDeleted({}, pending)).toBeNull();
    expect(dispatchSessionDeleted({ sessionId: "" }, pending)).toBeNull();
    expect(dispatchSessionDeleted({ sessionId: 42 }, pending)).toBeNull();
  });

  it("returns null on null / non-object payloads", () => {
    const pending = new Set([SESSION_ID]);
    expect(dispatchSessionDeleted(null, pending)).toBeNull();
    expect(dispatchSessionDeleted(undefined, pending)).toBeNull();
    expect(dispatchSessionDeleted("string-payload", pending)).toBeNull();
    expect(dispatchSessionDeleted(123, pending)).toBeNull();
  });
});

describe("classifyPollingResponse (PR5 finding 3 fallback)", () => {
  it("treats 404 as 'deleted' so a missed SignalR event still clears the spinner", () => {
    expect(classifyPollingResponse(404)).toBe("deleted");
  });

  it("treats 200 as 'wait' — cascade still in progress, do not remove the row", () => {
    expect(classifyPollingResponse(200)).toBe("wait");
  });

  it("treats transient errors as 'wait' — never falsely flag a deletion as complete", () => {
    // 5xx and auth errors must NOT clear a pending row; the next tick retries. The hook
    // would otherwise drop the spinner while the cascade is still running, then have to
    // re-add it when the row is still in the table on the next fetch.
    expect(classifyPollingResponse(401)).toBe("wait");
    expect(classifyPollingResponse(403)).toBe("wait");
    expect(classifyPollingResponse(429)).toBe("wait");
    expect(classifyPollingResponse(500)).toBe("wait");
    expect(classifyPollingResponse(502)).toBe("wait");
    expect(classifyPollingResponse(503)).toBe("wait");
  });

  it("treats 409 'session locked by cascade' as 'wait' — row still in the table by design", () => {
    // The GET endpoint returns 200 for a locked session (not 409), but defensively the
    // helper treats anything-not-404 as 'wait'. This pins the contract.
    expect(classifyPollingResponse(409)).toBe("wait");
  });
});
