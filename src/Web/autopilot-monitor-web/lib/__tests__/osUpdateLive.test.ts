import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import {
  AWAITING_SIGN_IN_SETTLE_MS,
  computeOsUpdateInterval,
  deriveOsUpdateLive,
  deviceNowMs,
  isUpdateActivityName,
  osUpdateLiveState,
  parseUtcMs,
  type LiveEvent,
  type LiveSession,
  type OsUpdateLiveState,
} from "../osUpdateLive";

interface CaseEvent {
  type: string;
  t: string;
  arrives?: string;
  phase?: keyof typeof SHARED_MANIFEST.enrollmentPhases;
  data?: Record<string, unknown>;
}

interface CaseCut {
  now: string;
  interval: { start: string; end: string; rebootCount: number; outcome: string; kbs: string[]; notInstalledKbs: string[] } | null;
  live: { state: OsUpdateLiveState; restarting: boolean } | null;
}

interface Scenario {
  name: string;
  session: { startedAt: string; resumedAt?: string; isPreProvisioned?: boolean };
  events: CaseEvent[];
  cuts: CaseCut[];
}

/** tests/fixtures/os-update-live at the repo root — the cases the backend calculator runs too. */
function loadScenarios(): Scenario[] {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, "AutopilotMonitor.sln"))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error("repository root (AutopilotMonitor.sln) not found");
    dir = parent;
  }
  return JSON.parse(fs.readFileSync(path.join(dir, "tests", "fixtures", "os-update-live", "cases.json"), "utf8")).scenarios;
}

function ms(iso: string): number {
  const value = parseUtcMs(iso);
  if (value === null) throw new Error(`not a timestamp: ${iso}`);
  return value;
}

/** The events the backend had before `nowMs`; the listing order is the sequence. */
function eventsAt(scenario: Scenario, nowMs: number): LiveEvent[] {
  return scenario.events.flatMap((e, i) =>
    ms(e.arrives ?? e.t) < nowMs
      ? [{
          eventType: e.type,
          timestamp: e.t,
          sequence: i + 1,
          phase: SHARED_MANIFEST.enrollmentPhases[e.phase ?? "Unknown"],
          data: e.data ?? {},
          source: "",
        }]
      : []);
}

function liveSession(scenario: Scenario): LiveSession {
  return { status: "InProgress", ...scenario.session };
}

const scenarios = loadScenarios();
const cuts = scenarios.flatMap(s => s.cuts.map(c => [s.name, c.now, s, c] as const));

describe("OOBE update interval — shared cases (web = backend calculator)", () => {
  it("has the scenarios, and every label appears", () => {
    expect(scenarios.length).toBeGreaterThanOrEqual(15);
    const labels = new Set(cuts.map(([, , , c]) => c.live?.state ?? null));
    for (const label of ["updating", "awaiting_sign_in", "ended", null]) expect(labels).toContain(label);
    expect(cuts.some(([, , , c]) => c.live?.restarting)).toBe(true);
  });

  it.each(cuts)("%s at %s: the interval the time attribution reports", (_name, now, s, c) => {
    const nowMs = ms(now);
    const startMs = ms(s.session.resumedAt ?? s.session.startedAt);
    const events = eventsAt(s, nowMs);

    const closed = computeOsUpdateInterval(events, { startMs, endMs: nowMs });
    if (c.interval === null) {
      expect(closed).toBeNull();
    } else {
      expect(closed).not.toBeNull();
      expect({
        start: closed!.startMs,
        end: closed!.endMs,
        rebootCount: closed!.rebootCount,
        outcome: closed!.outcome,
        kbs: closed!.kbs,
        notInstalledKbs: closed!.notInstalledKbs,
      }).toEqual({ ...c.interval, start: ms(c.interval.start), end: ms(c.interval.end) });
    }

    // The open window the live hint uses reads the same update; only the bound is unknown.
    const open = computeOsUpdateInterval(events, { startMs, endMs: Number.POSITIVE_INFINITY });
    expect(open === null ? null : { ...open, boundMs: null }).toEqual(closed === null ? null : { ...closed, boundMs: null });
  });

  it.each(cuts)("%s at %s: the live label", (_name, now, s, c) => {
    const nowMs = ms(now);
    const facts = deriveOsUpdateLive(eventsAt(s, nowMs), liveSession(s));
    const state = facts ? osUpdateLiveState(facts, nowMs) : null;

    expect(state).toBe(c.live?.state ?? null);
    if (c.live) expect(facts!.restarting).toBe(c.live.restarting);
  });
});

// ── the live derivation beyond the shared cases ──────────────────────────────

const updating = scenarios.find(s => s.name === "wait_for_sign_in_with_standby")!;
const updatingNow = ms("2026-10-02T14:35:00Z");

describe("deriveOsUpdateLive", () => {
  it("shows nothing once the session is terminal", () => {
    const events = eventsAt(updating, updatingNow);
    expect(deriveOsUpdateLive(events, liveSession(updating))).not.toBeNull();
    for (const status of ["Succeeded", "Failed", "Incomplete"]) {
      expect(deriveOsUpdateLive(events, { ...liveSession(updating), status })).toBeNull();
    }
  });

  it("shows nothing for a pre-provisioned device parked after its technician part", () => {
    const events = [
      ...eventsAt(updating, updatingNow),
      { eventType: "whiteglove_complete", timestamp: "2026-10-02T14:34:00Z", sequence: 999, phase: -1, data: {}, source: "" },
    ];
    const parked: LiveSession = { status: "InProgress", startedAt: updating.session.startedAt, isPreProvisioned: true };
    expect(deriveOsUpdateLive(events, parked)).toBeNull();
    expect(deriveOsUpdateLive(events, { ...parked, resumedAt: "2026-10-02T14:10:00Z" })).not.toBeNull();
  });

  it("shows nothing without a session start", () => {
    expect(deriveOsUpdateLive(eventsAt(updating, updatingNow), { status: "InProgress" })).toBeNull();
  });

  it("orders by sequence, not by the order the events came in", () => {
    const events = eventsAt(updating, updatingNow);
    const shuffled = [...events].reverse();
    expect(deriveOsUpdateLive(shuffled, liveSession(updating))).toEqual(deriveOsUpdateLive(events, liveSession(updating)));
  });

  it("knows when the device last reported", () => {
    const facts = deriveOsUpdateLive(eventsAt(updating, updatingNow), liveSession(updating))!;
    expect(facts.lastReportMs).toBe(ms("2026-10-02T14:30:00Z"));
  });

  it("does not take a backfilled row's time for the last report", () => {
    // A sleep episode read back from the event log can carry a time hours ahead of the enrollment.
    const sleep = { eventType: "system_sleep_episode", timestamp: "2026-10-02T22:00:00Z", sequence: 998, phase: -1, data: {}, source: "SystemTimelineWatcher" };
    const facts = deriveOsUpdateLive([...eventsAt(updating, updatingNow), sleep], liveSession(updating))!;
    expect(facts.lastReportMs).toBe(ms("2026-10-02T14:30:00Z"));
    expect(deviceNowMs(facts, updatingNow)).toBe(updatingNow);
  });

  it("has no last report without an activity event, and then trusts the browser clock", () => {
    const onlyBackfill = eventsAt(updating, updatingNow).map(e => ({ ...e, source: "ShellCoreTracker" }));
    const facts = deriveOsUpdateLive(onlyBackfill, liveSession(updating))!;
    expect(facts.lastReportMs).toBeNull();
    expect(deviceNowMs(facts, updatingNow)).toBe(updatingNow);
  });
});

describe("osUpdateLiveState", () => {
  const waitNow = ms("2026-10-02T14:46:00Z");
  const facts = deriveOsUpdateLive(eventsAt(updating, waitNow), liveSession(updating))!;

  it("waits for sign-in only once the settle time after the update's end has passed", () => {
    const end = facts.interval.endMs;
    expect(osUpdateLiveState(facts, end + AWAITING_SIGN_IN_SETTLE_MS - 1)).toBe("updating");
    expect(osUpdateLiveState(facts, end + AWAITING_SIGN_IN_SETTLE_MS)).toBe("awaiting_sign_in");
  });

  it("measures from the device clock: never before the newest activity event", () => {
    const last = ms("2026-10-02T14:43:00Z");
    expect(facts.lastReportMs).toBe(last);
    expect(deviceNowMs(facts, last - 60_000)).toBe(last);
    expect(deviceNowMs(facts, last + 60_000)).toBe(last + 60_000);
  });
});

describe("parseUtcMs", () => {
  it("reads a timestamp without a zone as UTC, as the backend does", () => {
    expect(parseUtcMs("2026-10-02T14:31:00.0000000")).toBe(Date.UTC(2026, 9, 2, 14, 31));
    expect(parseUtcMs("2026-10-02T14:31:00Z")).toBe(Date.UTC(2026, 9, 2, 14, 31));
    expect(parseUtcMs("2026-10-02T16:31:00+02:00")).toBe(Date.UTC(2026, 9, 2, 14, 31));
  });

  it("keeps the milliseconds of a 7-digit fraction", () => {
    expect(parseUtcMs("2026-10-02T14:20:02.1234567Z")).toBe(Date.UTC(2026, 9, 2, 14, 20, 2, 123));
  });

  it("returns null for anything else", () => {
    for (const raw of [null, undefined, "", "not a time", 42]) expect(parseUtcMs(raw)).toBeNull();
  });
});

describe("isUpdateActivityName — the names only an update writes (backend IsUpdateActivityName)", () => {
  it.each([
    ["ExpeditedUpdate_commitExpeditionDownloadInstallAsyncStarted", true],
    ["SdxWebAppCloudNDUP_processStatusChangeFromHandler_downloadSucceeded", true],
    ["SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededRebootRequired_lcu", true],
    ["SdxWebAppCloudNDUP_updateUSOProgressBar_DownloadPhase_progress100", true],
    ["SdxWebAppCloudNDUP_downloadInstallFailureHelper", true],
    ["SdxWebAppCloudNDUP_rebootCountdown_starting", true],
    ["SdxWebAppCloudNDUP_initialize_NDUPInstallCanceledInOptOut", false],
    ["SdxWebAppCloudNDUP_initialize_NDUPDownloadInstallPreviousFailureCount", false],
    ["SdxWebAppCloudNDUP_processStatusChangeFromHandler_installSucceededNoReboot", false],
    ["SdxWebAppCloudNDUP_handleInstallHelper", false],
    ["ExpeditedUpdate_getUpdateResultsSucceeded", false],
    [null, false],
  ] as const)("%s → %s", (name, expected) => {
    expect(isUpdateActivityName(name)).toBe(expected);
  });
});
