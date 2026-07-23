/**
 * Pure logic for the ScriptExecutions panel: turning a stream of script_started /
 * script_completed / script_failed events into the deduped, ordered list of items
 * the UI renders. Extracted from the React component so the 2-pass reducer + label
 * mapping can be unit-tested without React Testing Library.
 */
import { extractBootstrapVersion } from "@/utils/bootstrapVersion";
import { HISTORIC_REPLAY_THRESHOLD_MS, partitionHistoricReplayEvents } from "./historicReplay";

export interface ScriptInputEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

export interface ScriptItem {
  policyId: string;
  scriptType: string;        // "platform" | "remediation"
  scriptPart?: string;        // "detection" | "remediation" | "post-detection"
  runContext?: string;
  exitCode?: number;
  result?: string;
  complianceResult?: string;
  remediationStatus?: number;
  targetType?: number;
  errorCode?: number;
  errorDetails?: string;
  stdout?: string;
  stderr?: string;
  /**
   * Actual script run time in seconds (start line → the script's own end signal). Only
   * populated from events whose durationBasis is "script_runtime" — platform scripts and
   * health-script early-signal (HS-COMPLIANCE) completions. Absent when the agent never
   * saw the start line, or when only the cycle-basis value is known (see
   * reportedAfterSeconds).
   */
  durationSeconds?: number;
  /**
   * Whole-cycle duration in seconds measured to the HS-NEW-RESULT line, which IME only
   * writes after its batched report to the Microsoft service — systematically longer than
   * the scripts actually ran (30 s – minutes of reporting latency). Populated from events
   * with durationBasis "cycle_including_reporting_latency" (and from legacy remediation
   * events that predate the basis field, which measured the same span). Shown as
   * "Reported after Xm" in the details panel, never as the run duration.
   */
  reportedAfterSeconds?: number;
  state: "Running" | "Success" | "Failed";
  timestamp: string;
  bootstrapVersion?: string | null;
}

/**
 * Compact human-readable run duration (e.g. "8s", "2m 14s", "1h 03m"). Returns null for
 * null/negative input so callers can omit the cell entirely.
 */
export function formatScriptDuration(seconds?: number): string | null {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return null;
  const total = Math.round(seconds);
  if (total < 60) return `${total}s`;
  const m = Math.floor(total / 60);
  const s = total % 60;
  if (m < 60) return `${m}m ${s.toString().padStart(2, "0")}s`;
  const h = Math.floor(m / 60);
  const rm = m % 60;
  return `${h}h ${rm.toString().padStart(2, "0")}m`;
}

/**
 * Stable React key for a ScriptItem. Based ONLY on identity-shaped fields (policyId,
 * scriptType, scriptPart) so re-renders triggered by upstream prop changes (live event
 * polling, SignalR) keep the same component instance mounted — preserving showDetails
 * and other local state. Do NOT include timestamp or insertion index here, otherwise
 * any reducer re-run will remount and collapse expanded detail panels.
 */
export function scriptItemKey(item: Pick<ScriptItem, "policyId" | "scriptType" | "scriptPart" | "state">): string {
  const part = item.state === "Running" ? "_running" : (item.scriptPart ?? "_nopart");
  return `${item.policyId || "_noid"}-${item.scriptType}-${part}`;
}

/**
 * A logical script card shown in the UI. Platform scripts and detect-only / running
 * remediations render as a single-phase card; non-compliant remediation cycles group
 * the 2-3 phases (detection / remediation / post-detection) under one parent so the
 * cycle reads as one unit rather than three disconnected rows. The header carries the
 * cycle-level outcome derived across all phases + RemediationStatus.
 */
export interface ScriptCard {
  policyId: string;
  scriptType: string;        // "platform" | "remediation"
  /**
   * The phase items that make up this card, sorted in their natural cycle order
   * (detection → remediation → post-detection). For platform / single-phase cards
   * this contains exactly one item.
   */
  phases: ScriptItem[];
  /** Header outcome label, e.g. "Compliant", "Remediated successfully", "Remediation failed". */
  headerLabel: string;
  /** Overall card state — drives the container colour. */
  headerState: "Running" | "Success" | "Failed" | "NonCompliant";
  /** True when the card represents a multi-phase remediation cycle (header expandable). */
  isCycle: boolean;
  /** First-phase timestamp; used for sorting cards chronologically in the UI. */
  timestamp: string;
  /**
   * Card-level actual run time in seconds. Phases carry cumulative script_runtime durations
   * measured from the same cycle start (detection ≤ post-detection), so the max across
   * phases is the execution time through the latest observed phase. Shown once on the card
   * header rather than repeated on each nested phase row. Absent when no phase reported a
   * runtime-basis duration (e.g. legacy events that only carried the cycle-basis value).
   */
  durationSeconds?: number;
}

/** Stable React key for a ScriptCard. */
export function scriptCardKey(card: Pick<ScriptCard, "policyId" | "scriptType" | "headerState">): string {
  const idPart = card.headerState === "Running" ? "_running" : "_card";
  return `${card.policyId || "_noid"}-${card.scriptType}-${idPart}`;
}

/** Threshold above which a Running placeholder is rendered as "stuck?" rather than animated. */
export const STALE_RUNNING_THRESHOLD_SECONDS = 600;

export { HISTORIC_REPLAY_THRESHOLD_MS };

const SCRIPT_FINAL_TYPES: ReadonlySet<string> = new Set(["script_completed", "script_failed"]);

export interface HistoricPartition {
  current: ScriptInputEvent[];
  /**
   * Count of dropped FINAL events (script_completed / script_failed) — what the user reads
   * as "hidden executions". Dropped script_started events are removed but not counted, so
   * the note is not inflated by start/end pairs of the same run.
   */
  historicCount: number;
}

/**
 * Script-specific wrapper over `partitionHistoricReplayEvents` (see lib/historicReplay.ts
 * for the replay semantics): drops script events replayed from a previous enrollment
 * (session eaf3d8c4: 156 replayed runs shown with ~170 h durations) and counts the finals
 * for the "N hidden" note.
 */
export function partitionHistoricScriptEvents(events: ScriptInputEvent[]): HistoricPartition {
  return partitionHistoricReplayEvents(events, SCRIPT_FINAL_TYPES);
}

/**
 * Score how complete a script item's data is (higher = better). Used by the reducer to
 * pick the best entry when re-emissions of the same script collapse into one row.
 * Counts the presence of fields that meaningfully describe the script's outcome.
 */
function dataCompleteness(item: ScriptItem): number {
  let score = 0;
  if (item.exitCode != null) score += 4; // exit code is the most important signal
  if (item.result) score += 2;
  if (item.complianceResult) score += 2;
  if (item.remediationStatus != null) score += 1;
  if (item.stdout && item.stdout.length > 0) score += 1;
  if (item.stderr && item.stderr.length > 0) score += 1;
  if (item.runContext) score += 1;
  if (item.durationSeconds != null) score += 1;
  if (item.reportedAfterSeconds != null) score += 1;
  return score;
}

/**
 * Coerce a wire value to a number. Backend serializes integer event-data fields as strings
 * (`Dictionary<string, string>` payload format), but newer in-process emitters could pass
 * raw numbers. Accept both shapes so the reducer doesn't quietly drop fields based on the
 * wire encoding. Returns undefined for null / undefined / non-numeric strings.
 */
export function toNumber(v: unknown): number | undefined {
  if (typeof v === "number" && Number.isFinite(v)) return v;
  if (typeof v === "string" && v.length > 0) {
    const n = Number(v);
    return Number.isFinite(n) ? n : undefined;
  }
  return undefined;
}

/** Map RemediationStatus enum to the human-readable label shown in the detail panel. */
export function mapRemediationStatus(status?: number): string | null {
  switch (status) {
    case 0: return "Unknown";
    case 1: return "Compliant";
    case 2: return "Remediated";
    case 3: return "RemediationFailed";
    case 4: return "NoRemediation";
    default: return null;
  }
}

/**
 * Pick the headline label for a row. We use Intune Admin Center terminology — proactive
 * remediation policies are labelled "Remediation" in the Intune UI, regardless of which
 * phase (detection / remediation / post-detection) of the cycle this row represents.
 * The phase is conveyed via the badge next to the title, not the title itself.
 */
export function buildScriptItemLabel(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "state" | "remediationStatus">): string {
  if (item.scriptType === "remediation") {
    return item.state === "Running" ? "Remediation (running)" : "Remediation";
  }
  return item.state === "Running" ? "Platform Script (running)" : "Platform Script";
}

/**
 * Phase badge text for remediation rows (e.g. "detection", "remediation", "post-detection").
 * Returns null when no phase badge should render (platform scripts, Running placeholders).
 */
export function getPhaseBadge(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "state">): string | null {
  if (item.scriptType !== "remediation") return null;
  if (item.state === "Running") return null;
  if (!item.scriptPart) return null;
  return item.scriptPart;
}

/** True when this row should display the "detect-only" badge. */
export function isDetectOnlyRow(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "remediationStatus" | "state">): boolean {
  return item.state !== "Running"
    && item.scriptType === "remediation"
    && item.scriptPart === "detection"
    && item.remediationStatus === 4;
}

/**
 * True when this row represents a non-compliant health-script reading (detection or
 * post-detection that returned exit != 0). The script ran successfully — state stays
 * "Success" — but the compliance verdict is False, so the UI should style it amber to
 * draw attention without crying "failure".
 */
export function isNonCompliantReport(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "complianceResult" | "state">): boolean {
  return item.state === "Success"
    && item.scriptType === "remediation"
    && (item.scriptPart === "detection" || item.scriptPart === "post-detection")
    && item.complianceResult === "False";
}

/**
 * 2-pass reducer:
 *   1. Sort events by timestamp; collect every script_completed / script_failed final.
 *      Dedupe by (policyId, scriptType, scriptPart) so re-fetched events don't double-render.
 *   2. For every script_started whose policyId hasn't been finalized yet, append a "Running"
 *      placeholder. When a final lands later (SignalR or re-fetch) the placeholder vanishes
 *      naturally on the next render because the second pass re-runs.
 *
 * Returns rows in insertion order: finals first (sorted by timestamp), then any live
 * placeholders. The component sees a stable, deduped list with optimistic live indicators.
 */
export function reduceScriptEvents(events: ScriptInputEvent[]): ScriptItem[] {
  if (events.length === 0) return [];

  const sorted = [...events].sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );

  // Map of dedupe-key → ScriptItem. Same (policyId, scriptType, scriptPart) re-emissions
  // collapse into one row keeping the entry with the most-complete data. This is the
  // common case in IME logs where the same script appears multiple times across ESP-phase
  // transitions, sometimes with degraded data on the second emission (lost exit code etc).
  // For Autopilot enrollment sessions (time-bounded, < 1 h typical) this is the right
  // trade-off — long-running scheduled-script monitoring is out of scope for this UI.
  const finalsByKey = new Map<string, ScriptItem>();
  const policyIdsWithFinal = new Set<string>();

  for (let idx = 0; idx < sorted.length; idx++) {
    const evt = sorted[idx];
    if (evt.eventType !== "script_completed" && evt.eventType !== "script_failed") continue;
    const d = evt.data;
    if (!d) continue;

    const policyId = d.policyId ?? d.policy_id ?? "";
    const scriptType = d.scriptType ?? d.script_type ?? "platform";
    const scriptPart = d.scriptPart ?? d.script_part;
    const dedupeId = policyId || `_noid_${idx}`;
    const key = `${dedupeId}-${scriptType}-${scriptPart ?? ""}`;
    if (policyId) policyIdsWithFinal.add(`${policyId}-${scriptType}`);

    const exitCode = toNumber(d.exitCode ?? d.exit_code);

    // Duration normalization — the wire field durationSeconds carries two different
    // semantics, named by durationBasis (see the ScriptItem field docs):
    //   "script_runtime"                     → actual run time → durationSeconds
    //   "cycle_including_reporting_latency"  → HS-NEW-RESULT stamp incl. IME's batched
    //                                          reporting delay → reportedAfterSeconds
    // Legacy events predate the basis field: platform durations were always measured to
    // the script's own result line (runtime), remediation durations to HS-NEW-RESULT
    // (cycle) — infer accordingly so old data is never displayed as run time when it
    // isn't one.
    // Scripts cannot run > 24 h inside an enrollment (IME's execution timeout is ~30 min);
    // larger values are mixed-timeline artifacts from legacy agents (raw stale start paired
    // with a clock-clamped completion — session eaf3d8c4 showed 170 h). Hide, don't lie.
    const rawDurationUnchecked = toNumber(d.durationSeconds ?? d.duration_seconds);
    const rawDuration = rawDurationUnchecked != null && rawDurationUnchecked <= HISTORIC_REPLAY_THRESHOLD_MS / 1000
      ? rawDurationUnchecked
      : undefined;
    const rawBasis = d.durationBasis ?? d.duration_basis;
    const isCycleBasis = typeof rawBasis === "string"
      ? rawBasis === "cycle_including_reporting_latency"
      : scriptType === "remediation";
    const durationSeconds = isCycleBasis ? undefined : rawDuration;
    const reportedAfterSeconds = isCycleBasis ? rawDuration : undefined;

    const remediationStatus = toNumber(d.remediationStatus ?? d.remediation_status);
    const targetType = toNumber(d.targetType ?? d.target_type);
    const errorCode = toNumber(d.errorCode ?? d.error_code);
    const stdout = typeof d.stdout === "string" ? d.stdout : undefined;
    const stderr = typeof d.stderr === "string" ? d.stderr : undefined;
    const hasStderr = !!stderr && stderr.trim().length > 0;

    // State derivation — rules in priority order (mirrors the agent's EmitScriptCompleted):
    //   1. Phase-aware exemption: detection / post-detection are *compliance reports*, not
    //      crash signals. Their outcome is the compliance verdict, so NEITHER a non-zero exit
    //      NOR stderr forces Failed for those phases — detection PowerShell routinely leaks
    //      benign probe errors to stderr while still reporting compliant. Non-compliance is
    //      conveyed by complianceResult and rendered amber via isNonCompliantReport, not Failed.
    //   2. Otherwise (platform scripts, the remediation phase) stderr OR non-zero exit → Failed.
    //      Per user preference (debrief 2026-05-11): stderr on those scripts wants visibility.
    //   3. Defensive: explicit script_failed eventType OR result === "Failed" → Failed.
    const isHealthComplianceReport = scriptType === "remediation"
      && (scriptPart === "detection" || scriptPart === "post-detection");
    const isFailureSignal = evt.eventType === "script_failed"
      || d.result === "Failed"
      || (!isHealthComplianceReport && (hasStderr || (exitCode != null && exitCode !== 0)));

    const candidate: ScriptItem = {
      policyId,
      scriptType,
      scriptPart,
      runContext: d.runContext ?? d.run_context,
      exitCode,
      result: d.result,
      complianceResult: d.complianceResult ?? d.compliance_result,
      remediationStatus,
      targetType,
      errorCode,
      errorDetails: d.errorDetails ?? d.error_details,
      stdout,
      stderr,
      durationSeconds,
      reportedAfterSeconds,
      state: isFailureSignal ? "Failed" : "Success",
      timestamp: evt.timestamp,
      bootstrapVersion: scriptType === "platform" ? extractBootstrapVersion(stdout) : null,
    };

    // Keep the most-complete entry, but merge the duration fields across both: the
    // early-signal emission carries the runtime, the later HS-NEW-RESULT emission the
    // cycle value — whichever entry wins on completeness must not drop the other's timing.
    const existing = finalsByKey.get(key);
    if (!existing) {
      finalsByKey.set(key, candidate);
    } else {
      const winner = dataCompleteness(candidate) > dataCompleteness(existing) ? candidate : existing;
      const loser = winner === candidate ? existing : candidate;
      winner.durationSeconds ??= loser.durationSeconds;
      winner.reportedAfterSeconds ??= loser.reportedAfterSeconds;
      finalsByKey.set(key, winner);
    }
  }

  // Assemble the ordered list of finals. Sort by the timestamp of the kept entry so
  // the timeline reflects actual chronology of the surviving events.
  const items: ScriptItem[] = Array.from(finalsByKey.values()).sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );

  // Suppression set for Running placeholders: track which (policyId, scriptType) pairs
  // already have a final, so a started signal that arrives after its own completion
  // doesn't surface a stale Running indicator.
  const finalTimestampsByPolicy = new Map<string, number[]>();
  for (const item of items) {
    if (!item.policyId) continue;
    const policyKey = `${item.policyId}-${item.scriptType}`;
    const ts = new Date(item.timestamp).getTime();
    const arr = finalTimestampsByPolicy.get(policyKey);
    if (arr) arr.push(ts);
    else finalTimestampsByPolicy.set(policyKey, [ts]);
  }

  // Running placeholders: emit one per (policyId, scriptType) when a started signal
  // exists without any final at-or-after its timestamp. Collapsed by policyId+type so
  // a single row shows "running" rather than one per started signal — matches the
  // collapsed-final dedupe semantics above.
  const runningEmitted = new Set<string>();
  for (const evt of sorted) {
    if (evt.eventType !== "script_started") continue;
    const d = evt.data;
    if (!d) continue;

    const policyId = d.policyId ?? d.policy_id ?? "";
    const scriptType = d.scriptType ?? d.script_type ?? "platform";
    if (!policyId) continue;

    const policyKey = `${policyId}-${scriptType}`;
    if (runningEmitted.has(policyKey)) continue;

    const startedTs = new Date(evt.timestamp).getTime();
    const finals = finalTimestampsByPolicy.get(policyKey);
    if (finals && finals.some(ts => ts >= startedTs)) continue;

    runningEmitted.add(policyKey);
    items.push({
      policyId,
      scriptType,
      state: "Running",
      timestamp: evt.timestamp,
      bootstrapVersion: null,
    });
  }

  return items;
}

// ─────────────────────────────────────────────────────────────────────────────
// Card grouping — folds multi-phase remediation cycles under a single header
// ─────────────────────────────────────────────────────────────────────────────

const PHASE_ORDER: Record<string, number> = {
  "detection": 0,
  "remediation": 1,
  "post-detection": 2,
};

function phaseSortKey(part?: string): number {
  if (!part) return 0;
  return PHASE_ORDER[part] ?? 99;
}

/**
 * Derive the human-readable header label + state for a remediation card based on its
 * phases and the cycle-level RemediationStatus. The label communicates the cycle
 * outcome at a glance; the state drives the card colour. Priority (highest wins):
 *   1. Any phase Running       → state=Running, label="Running"
 *   2. Any phase Failed (real script crash, e.g. remediation phase exit != 0 or stderr)
 *                               → state=Failed,  label="Remediation script failed"
 *   3. Has post-detection AND post-detection compliance=False
 *                               → state=NonCompliant, label="Non-compliant after remediation"
 *   4. Has remediation phase AND post-detection compliance=True
 *                               → state=Success,  label="Remediated successfully"
 *   5. detection-only AND compliance=False (RemediationStatus=4)
 *                               → state=NonCompliant, label="Non-compliant (detect-only)"
 *   6. detection-only AND compliance=True
 *                               → state=Success,  label="Compliant"
 *   7. Fallback
 *                               → state mirrors first phase, label = generic
 */
function deriveRemediationHeader(
  phases: ScriptItem[]
): { label: string; state: ScriptCard["headerState"] } {
  if (phases.some(p => p.state === "Running")) {
    return { label: "Running", state: "Running" };
  }

  const detection = phases.find(p => p.scriptPart === "detection");
  const remediation = phases.find(p => p.scriptPart === "remediation");
  const postDetection = phases.find(p => p.scriptPart === "post-detection");
  const remediationStatus = phases
    .map(p => p.remediationStatus)
    .find(s => s != null);

  // Real script crash (not a non-compliant compliance verdict). The reducer already
  // routes detection / post-detection non-zero-exit to state=Success-with-amber, so
  // any phase here in state=Failed means a genuine failure: stderr or exit on the
  // remediation phase or platform-style failure.
  const failedPhase = phases.find(p => p.state === "Failed");
  if (failedPhase) {
    if (failedPhase.scriptPart === "remediation") {
      return { label: "Remediation script failed", state: "Failed" };
    }
    return { label: "Script error", state: "Failed" };
  }

  // Multi-phase cycle (a remediation actually ran). The cycle outcome lives in the
  // post-detection compliance + RemediationStatus.
  if (remediation || postDetection) {
    if (postDetection?.complianceResult === "False") {
      return { label: "Non-compliant after remediation", state: "NonCompliant" };
    }
    if (postDetection?.complianceResult === "True") {
      return { label: "Remediated successfully", state: "Success" };
    }
    // Remediation ran but post-detection result not yet known — mid-flight or partial data.
    return { label: "Remediation ran", state: "Success" };
  }

  // Detect-only path — only the detection phase ever ran (RemediationStatus=4
  // = NoRemediation, OR no remediation script attached to the policy).
  if (detection) {
    const isDetectOnly = remediationStatus === 4
      || (remediation == null && postDetection == null);
    if (detection.complianceResult === "False") {
      return {
        label: isDetectOnly ? "Non-compliant (detect-only)" : "Non-compliant",
        state: "NonCompliant",
      };
    }
    if (detection.complianceResult === "True") {
      return {
        label: isDetectOnly ? "Compliant (detect-only)" : "Compliant",
        state: "Success",
      };
    }
  }

  // Fallback for unknown shapes.
  return { label: "Health script ran", state: "Success" };
}

/**
 * Group flat ScriptItems into card entries: remediation phases for the same policyId
 * collapse into one parent card with a phases array; platform scripts and lone
 * remediation entries (single phase) become single-phase cards. Cards are returned in
 * chronological order based on the earliest phase timestamp.
 *
 * This is the structural change that lets the UI render
 *   [ Remediation 99e1274b ── Non-compliant after remediation ]
 *     ⟶ detection (pre)
 *     ⟶ remediation
 *     ⟶ detection (post)
 * instead of three disconnected sibling rows.
 */
export function groupScriptItems(items: ScriptItem[]): ScriptCard[] {
  if (items.length === 0) return [];

  // Group by (policyId, scriptType). Empty policyId items get unique single-phase cards
  // so they never collide with each other.
  const byPolicy = new Map<string, ScriptItem[]>();
  for (let idx = 0; idx < items.length; idx++) {
    const item = items[idx];
    const key = item.policyId
      ? `${item.policyId}-${item.scriptType}`
      : `_noid_${idx}-${item.scriptType}`;
    const arr = byPolicy.get(key);
    if (arr) arr.push(item);
    else byPolicy.set(key, [item]);
  }

  const cards: ScriptCard[] = [];
  for (const phases of byPolicy.values()) {
    // Sort phases inside each card: detection → remediation → post-detection
    // (Running entries naturally sort first since they have no scriptPart).
    phases.sort((a, b) => phaseSortKey(a.scriptPart) - phaseSortKey(b.scriptPart));

    const first = phases[0];
    const isCycle = first.scriptType === "remediation" && phases.length > 1;

    let headerLabel: string;
    let headerState: ScriptCard["headerState"];
    if (first.scriptType === "remediation") {
      const derived = deriveRemediationHeader(phases);
      headerLabel = derived.label;
      headerState = derived.state;
    } else {
      // Platform scripts: header mirrors the single phase.
      headerState = first.state === "Running" ? "Running" : (first.state === "Failed" ? "Failed" : "Success");
      headerLabel = first.state === "Running"
        ? "Running"
        : (first.result ?? (first.state === "Failed" ? "Failed" : "Success"));
    }

    // Card-level run time: phase runtimes are cumulative from the same cycle start
    // (detection ≤ post-detection), so the max is the execution time through the latest
    // observed phase. Platform / single-phase cards inherit their lone phase's duration
    // (rendered on the row itself, not the header).
    const phaseDurations = phases
      .map(p => p.durationSeconds)
      .filter((d): d is number => d != null);
    const cardDuration = phaseDurations.length > 0 ? Math.max(...phaseDurations) : undefined;

    cards.push({
      policyId: first.policyId,
      scriptType: first.scriptType,
      phases,
      headerLabel,
      headerState,
      isCycle,
      timestamp: first.timestamp,
      durationSeconds: cardDuration,
    });
  }

  // Sort cards by earliest-phase timestamp so the timeline order is preserved.
  cards.sort((a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime());
  return cards;
}
