import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { computeServerTime, eventServerTimes, median, toServerTimeEvent, type ServerTimeEvent } from "@/lib/serverTime";

interface CaseEvent { seq: number; type: string; source: string; t: string; sent: string | null; recv: string | null }
interface Case {
  name: string;
  note: string;
  isPreProvisioned: boolean;
  completedAt: string | null;
  events: CaseEvent[];
  expect: {
    start: string | null;
    startOffset: number | null;
    end: string | null;
    duration: number | null;
    part1: number | null;
    part2: number | null;
    part2Start: string | null;
    eventServerTimes: { seq: number; server: string | null }[];
  };
}

/** tests/fixtures/server-time at the repo root — the cases the backend's ServerTime runs too. */
function loadCases(): Case[] {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, "AutopilotMonitor.sln"))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error("repository root (AutopilotMonitor.sln) not found");
    dir = parent;
  }
  return JSON.parse(fs.readFileSync(path.join(dir, "tests", "fixtures", "server-time", "cases.json"), "utf8")).cases;
}

const cases = loadCases();

function ms(iso: string | null): number | null {
  return iso == null ? null : Date.parse(iso);
}

function events(c: Case): ServerTimeEvent[] {
  return c.events.map((e) => ({ sequence: e.seq, eventType: e.type, source: e.source, time: ms(e.t)!, sentAt: ms(e.sent), receivedAt: ms(e.recv) }));
}

function expectTime(actual: number | null, expected: string | null, what: string) {
  if (expected == null) {
    expect(actual, what).toBeNull();
    return;
  }
  expect(actual, what).not.toBeNull();
  expect(Math.abs(actual! - Date.parse(expected)), `${what}: ${new Date(actual!).toISOString()} vs ${expected}`).toBeLessThanOrEqual(1);
}

function expectSeconds(actual: number | null, expected: number | null, what: string) {
  if (expected == null) {
    expect(actual, what).toBeNull();
    return;
  }
  expect(actual, what).not.toBeNull();
  expect(Math.abs(actual! - expected), `${what}: ${actual} vs ${expected}`).toBeLessThanOrEqual(0.001);
}

describe("server-time rule — shared cases", () => {
  it("has the cases the parity guard needs", () => {
    expect(cases.length).toBeGreaterThanOrEqual(20);
  });

  it.each(cases.map((c) => [c.name, c] as const))("%s", (_name, c) => {
    const evs = events(c);
    const r = computeServerTime(evs, ms(c.completedAt), c.isPreProvisioned);
    expectTime(r.start, c.expect.start, "start");
    expectTime(r.end, c.expect.end, "end");
    expectSeconds(r.startOffsetSeconds, c.expect.startOffset, "startOffset");
    expectSeconds(r.durationSeconds, c.expect.duration, "duration");
    expectSeconds(r.part1Seconds, c.expect.part1, "part1");
    expectSeconds(r.part2Seconds, c.expect.part2, "part2");
    expectTime(r.part2Start, c.expect.part2Start, "part2Start");

    const servers = eventServerTimes(evs);
    for (const probe of c.expect.eventServerTimes) {
      const index = evs.findIndex((e) => e.sequence === probe.seq);
      expectTime(servers[index], probe.server, `event ${probe.seq}`);
    }
  });
});

describe("median", () => {
  it.each([
    [[5], 5],
    [[5, 1], 3],
    [[9, -70_000, 3], 3],
  ])("median(%j) = %d", (values, expected) => {
    expect(median(values)).toBe(expected);
  });
});

describe("toServerTimeEvent", () => {
  it("reads the original device time of a clamped event and the upload's send and receive time", () => {
    const e = toServerTimeEvent({
      sequence: 7,
      eventType: "enrollment_complete",
      source: "DecisionEngine",
      timestamp: "2026-01-09T08:00:00Z",
      originalTimestamp: "2026-01-01T08:00:00Z",
      timestampClamped: true,
      sentAt: "2026-01-09T08:00:01Z",
      receivedAt: "2026-01-09T08:00:02Z",
    });
    expect(e).toEqual({
      sequence: 7,
      eventType: "enrollment_complete",
      source: "DecisionEngine",
      time: Date.parse("2026-01-01T08:00:00Z"),
      sentAt: Date.parse("2026-01-09T08:00:01Z"),
      receivedAt: Date.parse("2026-01-09T08:00:02Z"),
    });
  });

  it("keeps an event without send time (old agent) with null send and receive time", () => {
    const e = toServerTimeEvent({
      sequence: 1, eventType: "agent_started", source: "Agent", timestamp: "2026-01-01T08:00:00Z",
      timestampClamped: false,
    });
    expect(e?.sentAt).toBeNull();
    expect(e?.receivedAt).toBeNull();
  });
});
