/**
 * Durations in server time — port of the backend's `ServerTime`
 * (`src/Backend/AutopilotMonitor.Functions/Helpers/ServerTime.cs`).
 *
 * Device clocks are wrong in a measurable share of sessions: off by whole hours for the entire
 * session (time zone in the hardware clock), stepped mid-session by a program or a sync, or on a
 * different era after a restart. The order of events stays the sequence. Every upload carries the
 * device send time (`sentAt`) next to the server receive time (`receivedAt`), so its offset
 * (`sentAt − receivedAt`) is the device clock error minus the upload's latency, and an event's
 * server time is its device time minus that offset.
 *
 * The one definition is `tests/fixtures/server-time/cases.json` (with its README); a change goes
 * into the case file first, then into both implementations.
 */
import type { EnrollmentEvent } from "@/types";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import { parseUtcMs } from "@/lib/osUpdateLive";

type EventTypeName = (typeof SHARED_MANIFEST.eventTypes)[number];

const AGENT_STARTED = "agent_started" satisfies EventTypeName;
const WHITEGLOVE_COMPLETE = "whiteglove_complete" satisfies EventTypeName;
const TERMINAL: ReadonlySet<string> = new Set(["enrollment_complete", "enrollment_failed"] satisfies EventTypeName[]);
const INELIGIBLE: ReadonlySet<string> = new Set(SHARED_MANIFEST.sessionAnchorIneligibleSources);

/** An offset change of at least this much is a clock change, below it upload noise. */
export const FRAME_STEP_MS = 60_000;
/** The start never moves more than this before the first agent start (today's StartedAt guard). */
export const START_GUARD_MS = 2 * 3_600_000;
/** An end anchor must have been uploaded live: buffer wait within [0, this]. */
export const LIVE_WAIT_MS = 60_000;

/** One event as the rule needs it; times in epoch milliseconds. */
export interface ServerTimeEvent {
  sequence: number;
  eventType: string | null | undefined;
  source: string | null | undefined;
  /** Device event time (the original timestamp when the backend clamped it). */
  time: number;
  /** Device send time of the event's upload; null for agents without it. */
  sentAt: number | null;
  /** Server receive time of the event's upload. */
  receivedAt: number | null;
}

/**
 * Start and end in server time (epoch ms) and the seconds they give; null where the input does not allow a value.
 * `part2Start` is the WhiteGlove Part 2 start whenever the events show one, also while running and when Part 2
 * does not fit the duration (it starts after the end).
 */
export interface ServerTimeResult {
  start: number | null;
  startOffsetSeconds: number | null;
  end: number | null;
  durationSeconds: number | null;
  part1Seconds: number | null;
  part2Seconds: number | null;
  part2Start: number | null;
}

const NONE: ServerTimeResult = {
  start: null, startOffsetSeconds: null, end: null, durationSeconds: null, part1Seconds: null, part2Seconds: null,
  part2Start: null,
};

interface Upload {
  sent: number;
  recv: number;
  offset: number;
  minSequence: number;
  run: number;
  smooth: number;
  runHead: number;
  events: number[];
}

/** Median of one to three values (mean of the middle two for an even count). */
export function median(values: readonly number[]): number {
  if (values.length === 0) throw new Error("median of an empty set");
  const v = [...values].sort((a, b) => a - b);
  const mid = Math.floor(v.length / 2);
  return v.length % 2 === 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
}

function isEligible(e: ServerTimeEvent): boolean {
  return !INELIGIBLE.has(e.source ?? "");
}

function buildUploads(events: readonly ServerTimeEvent[]): { uploads: Upload[]; byRun: Map<number, Upload[]>; uploadOf: (Upload | null)[] } {
  const byKey = new Map<string, Upload>();
  events.forEach((e, i) => {
    if (e.sentAt == null || e.receivedAt == null) return;
    const key = `${e.sentAt}|${e.receivedAt}`;
    let u = byKey.get(key);
    if (!u) {
      u = { sent: e.sentAt, recv: e.receivedAt, offset: e.sentAt - e.receivedAt, minSequence: e.sequence, run: 0, smooth: 0, runHead: 0, events: [] };
      byKey.set(key, u);
    }
    u.events.push(i);
    u.minSequence = Math.min(u.minSequence, e.sequence);
  });

  const uploads = [...byKey.values()].sort((a, b) => a.recv - b.recv || a.minSequence - b.minSequence);
  let run = -1;
  for (const u of uploads) {
    if (u.events.some((i) => events[i].eventType === AGENT_STARTED)) run++;
    u.run = Math.max(run, 0);
  }

  const byRun = new Map<number, Upload[]>();
  for (const u of uploads) {
    const list = byRun.get(u.run) ?? [];
    list.push(u);
    byRun.set(u.run, list);
  }
  for (const list of byRun.values()) {
    const head = median(list.slice(0, 3).map((u) => u.offset));
    list.forEach((u, j) => {
      u.runHead = head;
      u.smooth = j < 2 ? head : median(list.slice(j - 2, j + 1).map((x) => x.offset));
    });
  }

  const uploadOf: (Upload | null)[] = events.map(() => null);
  for (const u of uploads) for (const i of u.events) uploadOf[i] = u;
  return { uploads, byRun, uploadOf };
}

/**
 * Session start, end and duration in server time (see the README of the case file).
 * `completedAtMs` is the stored device-clock completion, null while running.
 */
export function computeServerTime(
  events: readonly ServerTimeEvent[],
  completedAtMs: number | null,
  isPreProvisioned: boolean,
): ServerTimeResult {
  const { uploads, byRun, uploadOf } = buildUploads(events);
  if (uploads.length === 0) return NONE;

  const run0 = byRun.get(0)!;
  const startOffset = run0[0].runHead;
  const frame1: Upload[] = [];
  for (const u of run0) {
    if (Math.abs(u.smooth - startOffset) >= FRAME_STEP_MS) break;
    frame1.push(u);
  }

  // The smoothed frame test lags one upload behind a step, and that first post-step upload carries
  // device times of the new era: an upload whose own offset is off the start offset is trusted only
  // when the next upload is still inside frame 1 (a delayed upload, not a step).
  const candidates: number[] = [];
  frame1.forEach((u, j) => {
    const trusted = Math.abs(u.offset - startOffset) < FRAME_STEP_MS || j + 1 < frame1.length;
    if (!trusted) return;
    for (const i of u.events) if (isEligible(events[i])) candidates.push(i);
  });

  const agentStarts = events
    .map((e, i) => i)
    .filter((i) => events[i].eventType === AGENT_STARTED && events[i].sentAt != null)
    .sort((a, b) => events[a].sequence - events[b].sequence);
  if (candidates.length === 0) return NONE;
  const reference = agentStarts.length > 0 ? events[agentStarts[0]].time : Math.min(...candidates.map((i) => events[i].time));
  const inGuard = candidates.map((i) => events[i].time).filter((t) => t >= reference - START_GUARD_MS);
  if (agentStarts.length > 0) inGuard.push(reference);
  const start = Math.min(...inGuard) - startOffset;
  const startOffsetSeconds = startOffset / 1000;

  // WhiteGlove Part 2 start: the first agent_started after the first whiteglove_complete, minus the start
  // offset of its run.
  const whiteGlove = events
    .map((e, i) => i)
    .filter((i) => events[i].eventType === WHITEGLOVE_COMPLETE && events[i].sentAt != null)
    .sort((a, b) => events[a].sequence - events[b].sequence)[0];
  let part2Start: number | null = null;
  if (whiteGlove !== undefined) {
    const uw = uploadOf[whiteGlove]!;
    const next = agentStarts.find((i) =>
      uploadOf[i]!.recv > uw.recv || (uploadOf[i]!.recv === uw.recv && events[i].sequence > events[whiteGlove].sequence));
    if (next !== undefined) part2Start = events[next].time - uploadOf[next]!.runHead;
  }
  const startOnly: ServerTimeResult = { ...NONE, start, startOffsetSeconds, part2Start };

  if (completedAtMs == null) return startOnly;

  const live = events
    .map((e, i) => i)
    .filter((i) => {
      const e = events[i];
      if (e.sentAt == null || e.receivedAt == null || !isEligible(e)) return false;
      const wait = e.sentAt - e.time;
      return wait >= 0 && wait <= LIVE_WAIT_MS;
    });
  const match = live
    .filter((i) => Math.abs(events[i].time - completedAtMs) < 1000)
    .sort((a, b) =>
      (TERMINAL.has(events[a].eventType ?? "") ? 0 : 1) - (TERMINAL.has(events[b].eventType ?? "") ? 0 : 1)
      || events[a].sequence - events[b].sequence);
  let endIndex: number;
  if (match.length > 0) {
    endIndex = match[0];
  } else {
    const before = live.filter((i) => events[i].time <= completedAtMs);
    if (before.length === 0) return startOnly;
    endIndex = before.reduce((best, i) =>
      events[i].time > events[best].time || (events[i].time === events[best].time && events[i].sequence > events[best].sequence) ? i : best);
  }

  const end = events[endIndex].time - uploadOf[endIndex]!.smooth;
  let durationMs = end - start;
  let part1Seconds: number | null = null;
  let part2Seconds: number | null = null;
  if (isPreProvisioned && whiteGlove !== undefined && part2Start != null && part2Start <= end) {
    const part1End = events[whiteGlove].time - uploadOf[whiteGlove]!.smooth;
    part1Seconds = (part1End - start) / 1000;
    part2Seconds = (end - part2Start) / 1000;
    durationMs = part1End - start + (end - part2Start);
  }

  return { ...startOnly, end, durationSeconds: durationMs / 1000, part1Seconds, part2Seconds };
}

/** Server time (epoch ms) of every event, aligned with the input; null for events without send times. */
export function eventServerTimes(events: readonly ServerTimeEvent[]): (number | null)[] {
  const { uploadOf } = buildUploads(events);
  return events.map((e, i) => {
    const u = uploadOf[i];
    return u ? e.time - u.smooth : null;
  });
}

/** The rule's view of a wire event: device time (original when clamped), send and receive time. */
export function toServerTimeEvent(e: Pick<EnrollmentEvent, "sequence" | "eventType" | "source" | "timestamp" | "originalTimestamp" | "timestampClamped" | "sentAt" | "receivedAt">): ServerTimeEvent | null {
  const time = parseUtcMs(e.timestampClamped && e.originalTimestamp ? e.originalTimestamp : e.timestamp);
  if (time == null) return null;
  return {
    sequence: e.sequence ?? 0,
    eventType: e.eventType,
    source: e.source,
    time,
    sentAt: parseUtcMs(e.sentAt),
    receivedAt: parseUtcMs(e.receivedAt),
  };
}
