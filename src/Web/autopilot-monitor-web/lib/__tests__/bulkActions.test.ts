import { describe, it, expect } from "vitest";
import {
  runWithConcurrency,
  summarizeDeleteActions,
  summarizeBlockOutcomes,
} from "@/app/dashboard/hooks/bulkActions";
import type { DeleteResponseAction } from "@/app/dashboard/hooks/deleteSessionResponse";

/**
 * Pure-logic tests for the bulk delete / block helpers. The hooks compose these with
 * `authenticatedFetch` and the toast API; the helpers pin the concurrency contract and
 * the wording of the single summary toast a bulk run emits.
 */

const T = "11111111-1111-1111-1111-111111111111";

function queued(id: string): DeleteResponseAction {
  return { kind: "queued", sessionId: id, tenantId: T, manifestId: null };
}

describe("runWithConcurrency", () => {
  it("returns results in input order regardless of completion order", async () => {
    const delays = [30, 5, 15];
    const out = await runWithConcurrency(delays, 3, (d) => new Promise<number>((r) => setTimeout(() => r(d), d)));
    expect(out).toEqual([30, 5, 15]);
  });

  it("never runs more than `limit` items at once", async () => {
    let inFlight = 0;
    let peak = 0;
    const items = Array.from({ length: 10 }, (_, i) => i);
    await runWithConcurrency(items, 3, async () => {
      inFlight++;
      peak = Math.max(peak, inFlight);
      await new Promise((r) => setTimeout(r, 2));
      inFlight--;
    });
    expect(peak).toBe(3);
  });

  it("handles an empty input and a limit larger than the input", async () => {
    expect(await runWithConcurrency([], 3, async (x) => x)).toEqual([]);
    expect(await runWithConcurrency([1, 2], 10, async (x) => x * 2)).toEqual([2, 4]);
  });

  it("rejects when an item rejects", async () => {
    await expect(
      runWithConcurrency([1, 2], 2, async (x) => { if (x === 2) throw new Error("boom"); return x; }),
    ).rejects.toThrow("boom");
  });
});

describe("summarizeDeleteActions", () => {
  it("is an info toast when everything was queued", () => {
    const s = summarizeDeleteActions([queued("a"), queued("b")]);
    expect(s.type).toBe("info");
    expect(s.title).toBe("Deleting 2 sessions");
    expect(s.message).toMatch(/^2 queued\./);
  });

  it("treats notFound as success (row already gone)", () => {
    const s = summarizeDeleteActions([queued("a"), { kind: "notFound", sessionId: "b", message: "gone" }]);
    expect(s.type).toBe("info");
    expect(s.message).toMatch(/^1 queued, 1 already deleted\./);
  });

  it("is a warning when some sessions could not be queued", () => {
    const s = summarizeDeleteActions([
      queued("a"),
      { kind: "conflict", sessionId: "b", title: "Cascade already in flight", message: "m", hint: null },
      { kind: "unavailable", sessionId: "c", message: "m" },
      { kind: "error", sessionId: "d", message: "m" },
    ]);
    expect(s.type).toBe("warning");
    expect(s.message).toMatch(/^1 queued, 1 already in flight, 1 temporarily unavailable, 1 failed\./);
  });

  it("is an error toast when nothing succeeded", () => {
    const s = summarizeDeleteActions([{ kind: "error", sessionId: "a", message: "m" }]);
    expect(s.type).toBe("error");
  });
});

describe("summarizeBlockOutcomes", () => {
  it("reports a clean success", () => {
    const s = summarizeBlockOutcomes([{ ok: true }, { ok: true }]);
    expect(s.type).toBe("success");
    expect(s.message).toBe("2 devices blocked for 24 hours.");
  });

  it("reports partial failure with the first reason", () => {
    const s = summarizeBlockOutcomes([{ ok: true }, { ok: false, message: "HTTP 503" }, { ok: false, message: "other" }]);
    expect(s.type).toBe("warning");
    expect(s.message).toBe("1 blocked, 2 failed (HTTP 503).");
  });

  it("is an error toast when every block failed", () => {
    const s = summarizeBlockOutcomes([{ ok: false, message: "HTTP 403" }]);
    expect(s.type).toBe("error");
    expect(s.title).toBe("Block failed");
  });
});
