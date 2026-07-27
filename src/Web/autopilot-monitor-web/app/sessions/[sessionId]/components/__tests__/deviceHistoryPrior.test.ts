import { describe, it, expect } from "vitest";
import { selectPriorSessions, type DeviceSessionRefDto } from "../deviceHistoryPrior";

function ref(sessionId: string, startedAt: string, status = "Succeeded"): DeviceSessionRefDto {
  return {
    sessionId,
    startedAt,
    completedAt: null,
    status,
    enrollmentType: "v1",
    isPreProvisioned: false,
    durationSeconds: 900,
    adminMarked: false,
  };
}

// Server chain order: StartedAt + SessionId ascending.
const chain = [
  ref("s-old", "2026-07-01T08:00:00Z", "Failed"),
  ref("s-mid", "2026-07-10T08:00:00Z", "Failed"),
  ref("s-new", "2026-07-20T08:00:00Z"),
];

describe("selectPriorSessions", () => {
  it("counts only entries that started BEFORE the viewed session (Codex review: viewing an older session must not count successors)", () => {
    // Viewing the middle session: only s-old is a previous enrollment — s-new came later.
    const prior = selectPriorSessions(chain, "s-mid");
    expect(prior.map((r) => r.sessionId)).toEqual(["s-old"]);
  });

  it("returns an empty list when the viewed session is the oldest entry", () => {
    expect(selectPriorSessions(chain, "s-old")).toEqual([]);
  });

  it("counts every other entry when the viewed session is the newest", () => {
    expect(selectPriorSessions(chain, "s-new").map((r) => r.sessionId)).toEqual(["s-old", "s-mid"]);
  });

  it("anchors a live session (not in the chain) on the session row's startedAt", () => {
    const prior = selectPriorSessions(chain, "s-live", "2026-07-15T08:00:00Z");
    expect(prior.map((r) => r.sessionId)).toEqual(["s-old", "s-mid"]);
  });

  it("breaks a startedAt tie on sessionId, mirroring the server chain sort", () => {
    const tied = [ref("s-a", "2026-07-10T08:00:00Z"), ref("s-b", "2026-07-10T08:00:00Z")];
    expect(selectPriorSessions(tied, "s-b").map((r) => r.sessionId)).toEqual(["s-a"]);
    expect(selectPriorSessions(tied, "s-a")).toEqual([]);
  });

  it("falls back to all other entries when no anchor is available", () => {
    expect(selectPriorSessions(chain, "s-unknown").map((r) => r.sessionId)).toEqual([
      "s-old",
      "s-mid",
      "s-new",
    ]);
  });
});
