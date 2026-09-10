/**
 * Observation coverage of one session — a pure projection of events the agent already emits.
 *
 * The agent only sees an enrollment from the moment the IME bootstrapped it, and even inside
 * that window only as well as its IME log tracker, collectors, spool and upload path worked.
 * Every one of those degradations is an event today (`agent_late_start`, `ime_tracker_degraded`,
 * `collector_degraded`, `telemetry_upload_poisoned`, …), but the triage timeline ranks them as
 * ordinary warnings — first to fall off the 50-entry cap on a noisy session, and the Info-severity
 * ones (`agent_late_start` on a successful session, `diagnostics_uploaded`) never make it at all.
 *
 * `buildSessionCoverage` folds them into one block that `get_session_summary` places BEFORE the
 * key events, so a reader knows what was and was not observable before interpreting findings —
 * in particular before trusting the absence of an event. No backend field, no new event type:
 * the block is computed from the same event list the summary already fetches.
 */
import type { EnrollmentEvent } from './generated/wire-types.generated.js';

/** Event types the coverage block reads. Guarded against the catalog by event-types-drift.test.ts. */
export const COVERAGE_EVENT_TYPES: ReadonlySet<string> = new Set([
  'agent_started',
  'agent_late_start',
  'historic_ime_replay_detected',
  'ime_tracker_degraded',
  'ime_pattern_hits',
  'collector_degraded',
  'spool_pressure_detected',
  'telemetry_upload_poisoned',
  'telemetry_upload_blocked',
  'ingress_backpressure',
  'disk_space_low',
  'diagnostics_collecting',
  'diagnostics_uploaded',
  'diagnostics_upload_failed',
  'app_tracking_summary',
  'script_started',
  'script_completed',
  'script_failed',
]);

/**
 * `data.<key>` slices the coverage block needs from the events endpoint, appended to
 * SUMMARY_EVENT_FIELDS so the backend never ships whole payloads for a summary.
 */
export const COVERAGE_EVENT_FIELDS =
  'data.previousExitType,data.oobeStateAtAgentStart,' +
  'data.bootToAgentStartSeconds,data.agentUptimeSeconds,data.outcome,data.note,' +
  'data.earliestRejectedSourceTimestamp,' +
  'data.file,data.firstSkippedPattern,data.lineBudgetBreaks,data.regexTimeouts,data.oversizedLines,data.unanchoredPatterns,data.linesRead,' +
  'data.collector,data.reason,data.errorType,' +
  'data.pendingItemCount,data.pendingBytes,data.kind,data.itemCount,data.disk_free_gb,' +
  'data.truncated,data.includedFiles,data.includedBytes,data.skippedFiles,data.skippedByReason,data.problemsByKind,' +
  'data.installingNames,data.downloadingNames,' +
  // Scripts block; scriptType/script_type already ride in SUMMARY_EVENT_FIELDS' own list.
  'data.policyId,data.policy_id';

export interface CoverageWindow {
  /** Timestamp of the first `agent_started` — the earliest moment anything was observed live. */
  observedFrom: string | null;
  /** `session.startedAt` — back-dated to the earliest replayed IME log line when the agent arrived late. */
  sessionStartedAt: string | null;
  /** Seconds between `sessionStartedAt` and `observedFrom`; > 0 means the start was reconstructed from IME logs, not observed. */
  startedAtBackdatedSeconds: number | null;
  /** Number of `agent_started` events — every restart is a gap in observation. */
  agentStarts: number;
  /** Distinct `previousExitType` values reported by restarts (the first start has none). */
  previousExitTypes: string[];
  lateStart: {
    bootToAgentStartSeconds: number | null;
    agentUptimeSeconds: number | null;
    outcome: string | null;
    note: string | null;
  } | null;
  historicReplaySuppressed: { earliestRejectedSourceTimestamp: string | null } | null;
}

export interface ImeTrackerCoverage {
  degraded: boolean;
  file?: string | null;
  firstSkippedPattern?: string | null;
  lineBudgetBreaks?: number | null;
  regexTimeouts?: number | null;
  oversizedLines?: number | null;
  unanchoredPatterns?: number | null;
  /** Whole-session counters from `ime_pattern_hits` (emitted at termination), when present. */
  sessionTotals?: {
    linesRead: number | null;
    lineBudgetBreaks: number | null;
    regexTimeouts: number | null;
    oversizedLines: number | null;
  };
}

export interface CollectorDegradation {
  collector: string;
  reason: string;
  errorType: string | null;
  at: string | null;
}

export interface UploadCoverage {
  spoolPressure: boolean;
  poisonedItems: number;
  blocked: boolean;
  backpressureEpisodes: number;
  diskSpaceLow: boolean;
}

export type DiagnosticsStatus = 'uploaded' | 'failed' | 'skipped' | 'not_collected';

export interface DiagnosticsCoverage {
  status: DiagnosticsStatus;
  errorCode?: string;
  /** Present once the agent reports packaging counters (never file paths — those live in the ZIP). */
  truncated?: boolean;
  includedFiles?: number;
  skippedFiles?: number;
  skippedByReason?: Record<string, number>;
  problemsByKind?: Record<string, number>;
}

export interface AppsCoverage {
  /** Apps the agent's last summary still listed as installing when observation ended (terminal session only). */
  stillInstalling: string[];
  /** Same for downloading. */
  stillDownloading: string[];
}

export interface ScriptsCoverage {
  /** Scripts whose start line was seen but no result before observation ended (terminal session only). */
  stillRunning: { policyId: string; scriptType: string }[];
}

export interface SessionCoverage {
  window: CoverageWindow;
  imeTracker: ImeTrackerCoverage;
  collectors: CollectorDegradation[];
  upload: UploadCoverage;
  diagnostics: DiagnosticsCoverage;
  apps: AppsCoverage;
  scripts: ScriptsCoverage;
  /** One calibrated line per observation gap; empty means nothing reported a gap. */
  gaps: string[];
}

type Ev = Partial<EnrollmentEvent>;

function str(v: unknown): string | null {
  if (v === undefined || v === null) return null;
  const s = String(v).trim();
  return s.length > 0 ? s : null;
}

function num(v: unknown): number | null {
  if (v === undefined || v === null || v === '') return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}

function bool(v: unknown): boolean {
  if (typeof v === 'boolean') return v;
  return String(v ?? '').toLowerCase() === 'true';
}

function countMap(v: unknown): Record<string, number> | undefined {
  if (!v || typeof v !== 'object' || Array.isArray(v)) return undefined;
  const out: Record<string, number> = {};
  for (const [k, raw] of Object.entries(v as Record<string, unknown>)) {
    const n = num(raw);
    if (n !== null && n > 0) out[k] = n;
  }
  return Object.keys(out).length > 0 ? out : undefined;
}

function ms(ts: unknown): number | null {
  const s = str(ts);
  if (!s) return null;
  const t = Date.parse(s);
  return Number.isFinite(t) ? t : null;
}

function byTime(a: Ev, b: Ev): number {
  return (ms(a.timestamp) ?? 0) - (ms(b.timestamp) ?? 0) || ((a.sequence ?? 0) - (b.sequence ?? 0));
}

function fmtCounts(m: Record<string, number>): string {
  return Object.entries(m).map(([k, n]) => `${k}: ${n}`).join(', ');
}

function names(v: unknown): string[] {
  return Array.isArray(v) ? v.map(str).filter((s): s is string => s !== null) : [];
}

/** Mirror of the backend SessionStatus terminal set (utils/sessionStatus.ts in the web app). */
function isTerminalSessionStatus(status: unknown): boolean {
  return status === 'Succeeded' || status === 'Failed' || status === 'Incomplete';
}

/**
 * Folds the session's events (already stripped of historic-replay rows by the caller) into the
 * coverage block. Pure: no fetch, no clock.
 */
export function buildSessionCoverage(
  session: { startedAt?: string | null; status?: string | null },
  events: readonly Ev[],
): SessionCoverage {
  const byType = new Map<string, Ev[]>();
  for (const e of events) {
    const et = String(e.eventType ?? '');
    if (!COVERAGE_EVENT_TYPES.has(et)) continue;
    const list = byType.get(et);
    if (list) list.push(e);
    else byType.set(et, [e]);
  }
  for (const list of byType.values()) list.sort(byTime);
  const of = (type: string): Ev[] => byType.get(type) ?? [];
  const first = (type: string): Ev | undefined => of(type)[0];
  const last = (type: string): Ev | undefined => of(type)[of(type).length - 1];

  const gaps: string[] = [];

  // ── Observation window ──────────────────────────────────────────────────────
  const starts = of('agent_started');
  const observedFrom = str(starts[0]?.timestamp);
  const sessionStartedAt = str(session.startedAt);
  const observedMs = ms(observedFrom);
  const startedMs = ms(sessionStartedAt);
  const startedAtBackdatedSeconds =
    observedMs !== null && startedMs !== null ? Math.max(0, Math.round((observedMs - startedMs) / 1000)) : null;
  const previousExitTypes = [...new Set(starts.slice(1).map((e) => str(e.data?.previousExitType)).filter((x): x is string => !!x))];

  const late = last('agent_late_start');
  const lateStart = late
    ? {
        bootToAgentStartSeconds: num(late.data?.bootToAgentStartSeconds),
        agentUptimeSeconds: num(late.data?.agentUptimeSeconds),
        outcome: str(late.data?.outcome),
        note: str(late.data?.note),
      }
    : null;

  const replay = first('historic_ime_replay_detected');
  const historicReplaySuppressed = replay
    ? { earliestRejectedSourceTimestamp: str(replay.data?.earliestRejectedSourceTimestamp) }
    : null;

  if (!observedFrom) {
    gaps.push('No agent_started event in this session: the live observation window cannot be established (events may stem from a partial or replayed upload).');
  } else if (startedAtBackdatedSeconds !== null && startedAtBackdatedSeconds > 60) {
    gaps.push(
      `Session start is ${startedAtBackdatedSeconds} s before the first agent_started (${observedFrom}): the earlier part was reconstructed from IME log replay, not observed live.`,
    );
  }
  if (lateStart) {
    const detail = [
      lateStart.bootToAgentStartSeconds !== null ? `agent started ${lateStart.bootToAgentStartSeconds} s after boot` : null,
      lateStart.agentUptimeSeconds !== null ? `ran ${lateStart.agentUptimeSeconds} s` : null,
    ].filter(Boolean).join(', ');
    gaps.push(`Low observation coverage${detail ? ` (${detail})` : ''}: ${lateStart.note ?? 'the agent observed only the end state of this enrollment.'}`);
  }
  if (historicReplaySuppressed) {
    gaps.push(
      `IME log content from a previous enrollment (earliest ${historicReplaySuppressed.earliestRejectedSourceTimestamp ?? 'unknown'}) was suppressed; app and script events from before this enrollment are intentionally absent.`,
    );
  }
  if (starts.length > 1) {
    const exits = previousExitTypes.length > 0 ? ` (previous exit: ${previousExitTypes.join(', ')})` : '';
    gaps.push(`The agent started ${starts.length} times${exits}: signals during the restart gaps were not observed.`);
  }

  // ── IME log tracker ─────────────────────────────────────────────────────────
  const degradedEv = first('ime_tracker_degraded');
  const hits = last('ime_pattern_hits');
  const sessionTotals = hits
    ? {
        linesRead: num(hits.data?.linesRead),
        lineBudgetBreaks: num(hits.data?.lineBudgetBreaks),
        regexTimeouts: num(hits.data?.regexTimeouts),
        oversizedLines: num(hits.data?.oversizedLines),
      }
    : undefined;
  let imeTracker: ImeTrackerCoverage;
  if (degradedEv) {
    imeTracker = {
      degraded: true,
      file: str(degradedEv.data?.file),
      firstSkippedPattern: str(degradedEv.data?.firstSkippedPattern),
      lineBudgetBreaks: num(degradedEv.data?.lineBudgetBreaks),
      regexTimeouts: num(degradedEv.data?.regexTimeouts),
      oversizedLines: num(degradedEv.data?.oversizedLines),
      unanchoredPatterns: num(degradedEv.data?.unanchoredPatterns),
      ...(sessionTotals ? { sessionTotals } : {}),
    };
    const parts: string[] = [];
    const totals = sessionTotals ?? {
      linesRead: null,
      lineBudgetBreaks: imeTracker.lineBudgetBreaks,
      regexTimeouts: imeTracker.regexTimeouts,
      oversizedLines: imeTracker.oversizedLines,
    };
    if ((totals.lineBudgetBreaks ?? 0) > 0) {
      parts.push(`skipped the remaining patterns on ${totals.lineBudgetBreaks} line(s)${imeTracker.firstSkippedPattern ? ` (first skipped: ${imeTracker.firstSkippedPattern})` : ''}`);
    }
    if ((totals.regexTimeouts ?? 0) > 0) parts.push(`gave up on ${totals.regexTimeouts} line(s) after a regex timeout`);
    if ((totals.oversizedLines ?? 0) > 0) parts.push(`dropped ${totals.oversizedLines} oversized line(s)`);
    if (parts.length === 0) parts.push('skipped work on at least one line');
    gaps.push(
      `IME log tracker ${parts.join(', ')} in ${imeTracker.file ?? 'an IME log'}: the absence of an IME-derived event is not fully provable for this session.`,
    );
  } else {
    imeTracker = { degraded: false, ...(sessionTotals ? { sessionTotals } : {}) };
  }

  // ── Collectors ──────────────────────────────────────────────────────────────
  const collectors: CollectorDegradation[] = [];
  const seen = new Set<string>();
  for (const e of of('collector_degraded')) {
    const collector = str(e.data?.collector) ?? str(e.source) ?? 'unknown';
    const reason = str(e.data?.reason) ?? 'unknown';
    const key = `${collector}|${reason}`;
    if (seen.has(key)) continue;
    seen.add(key);
    collectors.push({ collector, reason, errorType: str(e.data?.errorType), at: str(e.timestamp) });
  }
  for (const c of collectors) {
    gaps.push(`Collector ${c.collector} degraded (${c.reason}${c.errorType ? `, ${c.errorType}` : ''}): its signals are missing from this session, not proven negative.`);
  }

  // ── Spool / upload ──────────────────────────────────────────────────────────
  const spool = last('spool_pressure_detected');
  const poisoned = of('telemetry_upload_poisoned');
  const poisonedItems = poisoned.reduce((n, e) => n + (num(e.data?.itemCount) ?? 1), 0);
  const blocked = of('telemetry_upload_blocked').length > 0;
  const backpressureEpisodes = of('ingress_backpressure').length;
  const disk = last('disk_space_low');
  const upload: UploadCoverage = {
    spoolPressure: !!spool,
    poisonedItems,
    blocked,
    backpressureEpisodes,
    diskSpaceLow: !!disk,
  };
  if (spool) {
    const pending = num(spool.data?.pendingItemCount);
    gaps.push(`Telemetry spool pressure${pending !== null ? ` (${pending} items pending)` : ''}: events reached the backend late; anything after the last upload may still be on the device.`);
  }
  if (poisonedItems > 0) {
    const kinds = [...new Set(poisoned.map((e) => str(e.data?.kind)).filter((x): x is string => !!x))];
    gaps.push(`${poisonedItems} telemetry item(s) were quarantined as poison and never uploaded${kinds.length > 0 ? ` (${kinds.join(', ')})` : ''}; they remain in the spool inside the diagnostics ZIP.`);
  }
  if (blocked) {
    gaps.push('Telemetry upload was blocked by a permanent backend rejection at some point; later events reached the backend only once the block cleared.');
  }
  if (disk) {
    const free = num(disk.data?.disk_free_gb);
    gaps.push(`System drive ran low on space${free !== null ? ` (${free} GB free)` : ''}: spool and diagnostics writes may have failed.`);
  }

  // ── Diagnostics package ─────────────────────────────────────────────────────
  const uploaded = last('diagnostics_uploaded');
  const failed = last('diagnostics_upload_failed');
  const collecting = last('diagnostics_collecting');
  let diagnostics: DiagnosticsCoverage;
  if (uploaded) {
    diagnostics = { status: 'uploaded' };
    const d = uploaded.data ?? {};
    if (d.truncated !== undefined || d.includedFiles !== undefined) {
      diagnostics.truncated = bool(d.truncated);
      const included = num(d.includedFiles);
      const skipped = num(d.skippedFiles);
      if (included !== null) diagnostics.includedFiles = included;
      if (skipped !== null) diagnostics.skippedFiles = skipped;
      const byReason = countMap(d.skippedByReason);
      const problems = countMap(d.problemsByKind);
      if (byReason) diagnostics.skippedByReason = byReason;
      if (problems) diagnostics.problemsByKind = problems;
      if (diagnostics.truncated) {
        gaps.push(`Diagnostics ZIP is truncated: ${skipped ?? 'some'} file(s) skipped by the packaging caps${byReason ? ` (${fmtCounts(byReason)})` : ''}; the full list is in _TRUNCATED.txt inside the ZIP.`);
      }
      if (problems) {
        gaps.push(`Diagnostics packaging hit problems (${fmtCounts(problems)}): those files are missing from the ZIP; package-manifest.txt names them.`);
      }
    }
  } else if (failed) {
    const errorCode = str(failed.data?.errorCode) ?? 'unknown';
    if (errorCode.startsWith('diagnostics_skipped_')) {
      diagnostics = { status: 'skipped', errorCode };
      gaps.push(`No diagnostics ZIP for this session: collection was skipped by configuration (${errorCode}).`);
    } else {
      diagnostics = { status: 'failed', errorCode };
      gaps.push(`Diagnostics upload failed (${errorCode}): no ZIP is available for this session.`);
    }
  } else {
    diagnostics = { status: 'not_collected' };
    if (collecting) {
      gaps.push('Diagnostics collection started but neither an upload nor a failure was reported: the agent may have died while packaging.');
    }
  }

  // ── Apps without a terminal state ───────────────────────────────────────────
  // The agent's last app_tracking_summary (the shutdown emit) names what was still in flight.
  // Only a terminal session turns that into a gap — while the session is live those apps are
  // simply still installing. Apps that never started are the starved rule's finding, not a gap.
  const apps: AppsCoverage = { stillInstalling: [], stillDownloading: [] };
  if (isTerminalSessionStatus(session.status)) {
    const summary = last('app_tracking_summary');
    apps.stillInstalling = names(summary?.data?.installingNames);
    apps.stillDownloading = names(summary?.data?.downloadingNames);
    const inFlight = (verb: string, list: string[]): void => {
      if (list.length === 0) return;
      gaps.push(
        `${list.length} app(s) still ${verb} when the agent stopped observing (${list.join(', ')}): outcome unknown — not observed to fail, not observed to finish.`,
      );
    };
    inFlight('installing', apps.stillInstalling);
    inFlight('downloading', apps.stillDownloading);
  }

  // ── Scripts without a terminal state ────────────────────────────────────────
  // The agent has no shutdown summary for scripts, so the stream itself is the evidence — the
  // same rule as the web panel's running placeholder: a script_started with no
  // script_completed/script_failed for the same policy (and type) at or after it.
  const scripts: ScriptsCoverage = { stillRunning: [] };
  if (isTerminalSessionStatus(session.status)) {
    const identity = (e: Ev): { key: string; policyId: string; scriptType: string } | null => {
      const policyId = str(e.data?.policyId) ?? str(e.data?.policy_id);
      if (!policyId) return null;
      const scriptType = str(e.data?.scriptType) ?? str(e.data?.script_type) ?? 'platform';
      return { key: `${policyId}-${scriptType}`, policyId, scriptType };
    };
    const finalsByKey = new Map<string, number[]>();
    for (const e of [...of('script_completed'), ...of('script_failed')]) {
      const id = identity(e);
      const t = ms(e.timestamp);
      if (!id || t === null) continue;
      const list = finalsByKey.get(id.key);
      if (list) list.push(t);
      else finalsByKey.set(id.key, [t]);
    }
    const listed = new Set<string>();
    for (const e of of('script_started')) {
      const id = identity(e);
      const t = ms(e.timestamp);
      if (!id || t === null || listed.has(id.key)) continue;
      if ((finalsByKey.get(id.key) ?? []).some((f) => f >= t)) continue;
      listed.add(id.key);
      scripts.stillRunning.push({ policyId: id.policyId, scriptType: id.scriptType });
    }
    if (scripts.stillRunning.length > 0) {
      const names = scripts.stillRunning.map((s) => `${s.scriptType} ${s.policyId}`).join(', ');
      gaps.push(
        `${scripts.stillRunning.length} script(s) still running when the agent stopped observing (${names}): outcome unknown — not observed to fail, not observed to finish.`,
      );
    }
  }

  return {
    window: {
      observedFrom,
      sessionStartedAt,
      startedAtBackdatedSeconds,
      agentStarts: starts.length,
      previousExitTypes,
      lateStart,
      historicReplaySuppressed,
    },
    imeTracker,
    collectors,
    upload,
    diagnostics,
    apps,
    scripts,
    gaps,
  };
}
