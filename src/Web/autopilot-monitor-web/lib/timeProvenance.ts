/**
 * CMTrace time-resolution provenance (P13): the agent's per-line self-anchoring stamps
 * CMTrace-derived events with how their UTC timestamp was produced — the raw local time
 * as written in the log, the applied UTC offset and its origin, the purely observational
 * per-file writer offset, and the clock-fallback markers. All values arrive as STRINGS
 * inside the event data payload (see docs/agent/cmtrace-time-resolution.md).
 *
 * The timeline sorts by sequence — always, never by time (user guardrail). A displayed
 * time that steps BACKWARDS within that order is therefore information, not a rendering
 * bug: a backdated log line, a staleness-clamp fallback, or an era-mixed log whose lines
 * were corrected with different offsets (session e9753578 carried ±9 h of exactly that).
 * `classifyTimeJump` names the phenomenon so the timeline can mark the spot instead of
 * leaving the reader to distrust the order.
 */

export interface TimeProvenance {
  /** Raw local timestamp as written in the log line (no zone suffix, by design). */
  sourceLocalTs: string | null;
  /** UTC offset in minutes that was APPLIED to resolve the timestamp. */
  sourceOffsetMinutes: number | null;
  /** How the applied offset was obtained: "bias" | "line-anchored" | "reader-zone-fallback" | retired "calibrated". */
  sourceOffsetOrigin: string | null;
  /**
   * Observational per-file measurement from the writer's own UTC lines — NOT the applied
   * correction, and sticky after era flip-backs (the monotonicity guard rejects newer
   * anchors), so it can lag reality. Never present it as "what was applied".
   */
  measuredWriterOffsetMinutes: number | null;
  /** "true" when the line's own timestamp was unusable and the agent clock was substituted. */
  derivedTimestamp: string | null;
  /** The line's own timestamp that the staleness clamp rejected (set alongside derivedTimestamp). */
  rejectedSourceTimestamp: string | null;
}

function readString(data: Record<string, unknown>, camel: string, snake: string): string | null {
  const raw = data[camel] ?? data[snake];
  return typeof raw === "string" && raw.length > 0 ? raw : null;
}

function readNumber(data: Record<string, unknown>, camel: string, snake: string): number | null {
  const raw = data[camel] ?? data[snake];
  if (typeof raw === "number") return Number.isFinite(raw) ? raw : null;
  if (typeof raw !== "string" || raw.length === 0) return null;
  const parsed = Number(raw);
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * Reads the provenance fields from an event's data payload. Returns null when NO
 * provenance key is present — which also covers `data` being undefined and the
 * DataJson-truncation fallback (`_rawDataJson`), where the payload is not an object
 * of parsed keys at all.
 */
export function readTimeProvenance(data: Record<string, unknown> | undefined | null): TimeProvenance | null {
  if (!data || typeof data !== "object") return null;
  const provenance: TimeProvenance = {
    sourceLocalTs: readString(data, "sourceLocalTs", "source_local_ts"),
    sourceOffsetMinutes: readNumber(data, "sourceOffsetMinutes", "source_offset_minutes"),
    sourceOffsetOrigin: readString(data, "sourceOffsetOrigin", "source_offset_origin"),
    measuredWriterOffsetMinutes: readNumber(data, "measuredWriterOffsetMinutes", "measured_writer_offset_minutes"),
    derivedTimestamp: readString(data, "derivedTimestamp", "derived_timestamp"),
    rejectedSourceTimestamp: readString(data, "rejectedSourceTimestamp", "rejected_source_timestamp"),
  };
  const hasAny =
    provenance.sourceLocalTs !== null ||
    provenance.sourceOffsetMinutes !== null ||
    provenance.sourceOffsetOrigin !== null ||
    provenance.measuredWriterOffsetMinutes !== null ||
    provenance.derivedTimestamp !== null ||
    provenance.rejectedSourceTimestamp !== null;
  return hasAny ? provenance : null;
}

/**
 * Row-badge threshold for a backwards step of the displayed time. Field-measured
 * (2026-08-20, 20 sessions): normal sessions max out at 0.6–1.7 min of interleaved-writer
 * jitter (the agent's own grid tolerance is 2 min), while every genuine offset phenomenon
 * starts at the 15-minute zone quantum. 5 min sits safely between the two — at this
 * threshold 12/12 normal sessions showed zero badges.
 */
export const BACKWARD_JUMP_THRESHOLD_MS = 5 * 60 * 1000;

export type TimeJumpCause = "era-offset" | "derived-timestamp" | "rejected-source" | null;

export interface TimeJump {
  /** Positive magnitude of the backwards step in milliseconds. */
  deltaMs: number;
  cause: TimeJumpCause;
}

export interface TimeJumpInput {
  displayTime: Date;
  provenance: TimeProvenance | null;
}

/**
 * Detects a backwards step of the displayed time versus the previous RENDERED row and
 * names its cause when the provenance identifies one. Cause precedence: a clock-fallback
 * (derived) timestamp explains the row by itself; a rejected source timestamp likewise;
 * only then is an offset difference between the two rows read as an era-mixed log.
 * Callers pass prev = null at phase-section starts (jump detection never spans sections).
 */
export function classifyTimeJump(
  prev: TimeJumpInput | null,
  curr: TimeJumpInput,
  thresholdMs: number = BACKWARD_JUMP_THRESHOLD_MS,
): TimeJump | null {
  if (!prev) return null;
  const deltaMs = prev.displayTime.getTime() - curr.displayTime.getTime();
  if (!Number.isFinite(deltaMs) || deltaMs < thresholdMs) return null;

  let cause: TimeJumpCause = null;
  if (curr.provenance?.derivedTimestamp) {
    cause = "derived-timestamp";
  } else if (curr.provenance?.rejectedSourceTimestamp) {
    cause = "rejected-source";
  } else if (
    prev.provenance?.sourceOffsetMinutes !== null &&
    prev.provenance?.sourceOffsetMinutes !== undefined &&
    curr.provenance?.sourceOffsetMinutes !== null &&
    curr.provenance?.sourceOffsetMinutes !== undefined &&
    prev.provenance.sourceOffsetMinutes !== curr.provenance.sourceOffsetMinutes
  ) {
    cause = "era-offset";
  }
  return { deltaMs, cause };
}
