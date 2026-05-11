/**
 * Pure logic for the ScriptExecutions panel: turning a stream of script_started /
 * script_completed / script_failed events into the deduped, ordered list of items
 * the UI renders. Extracted from the React component so the 2-pass reducer + label
 * mapping can be unit-tested without React Testing Library.
 */
import { extractBootstrapVersion } from "@/utils/bootstrapVersion";

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
  state: "Running" | "Success" | "Failed";
  timestamp: string;
  firstSeenIndex: number;
  bootstrapVersion?: string | null;
}

/** Threshold above which a Running placeholder is rendered as "stuck?" rather than animated. */
export const STALE_RUNNING_THRESHOLD_SECONDS = 600;

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

/** Pick the headline label for a row. Five distinct shapes — see in-code comments. */
export function buildScriptItemLabel(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "state" | "remediationStatus">): string {
  if (item.scriptType === "remediation") {
    if (item.state === "Running") return "Health Script (running)";
    if (item.scriptPart === "remediation") return "Remediation Run";
    if (item.scriptPart === "post-detection") return "Detection (post)";
    // Detection phase — same label whether detect-only (RemediationStatus=4) or pre-detection
    // of a full cycle. The detect-only badge handles the visual distinction.
    return "Health Detection";
  }
  return item.state === "Running" ? "Platform Script (running)" : "Platform Script";
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

  const items: ScriptItem[] = [];
  const seenFinal = new Set<string>();
  // Keyed by `${policyId}-${scriptType}-${timestamp}` so repeated runs of the same health
  // script policy in a long-lived session each get their own running placeholder slot.
  // We can't dedupe a "started"-only emission against a "completed" without a shared
  // run-id (IME doesn't expose one), so we fall back to nearest-timestamp matching: a
  // running placeholder vanishes if a final event with a timestamp >= the started
  // timestamp exists for the same (policyId, scriptType).
  const finalTimestampsByPolicy = new Map<string, number[]>();

  for (let idx = 0; idx < sorted.length; idx++) {
    const evt = sorted[idx];
    if (evt.eventType !== "script_completed" && evt.eventType !== "script_failed") continue;
    const d = evt.data;
    if (!d) continue;

    const policyId = d.policyId ?? d.policy_id ?? "";
    const scriptType = d.scriptType ?? d.script_type ?? "platform";
    const scriptPart = d.scriptPart ?? d.script_part;
    const dedupeId = policyId || `_noid_${idx}`;
    // Including the timestamp distinguishes repeated runs of the same policy in a
    // long-lived session (e.g. health scripts on a daily schedule). Re-fetched events
    // carry the same timestamp and still dedupe correctly.
    const key = `${dedupeId}-${scriptType}-${scriptPart ?? ""}-${evt.timestamp}`;
    if (seenFinal.has(key)) continue;
    seenFinal.add(key);
    if (policyId) {
      const policyKey = `${policyId}-${scriptType}`;
      const timestamps = finalTimestampsByPolicy.get(policyKey);
      const ts = new Date(evt.timestamp).getTime();
      if (timestamps) timestamps.push(ts);
      else finalTimestampsByPolicy.set(policyKey, [ts]);
    }

    const exitCode = toNumber(d.exitCode ?? d.exit_code);
    const remediationStatus = toNumber(d.remediationStatus ?? d.remediation_status);
    const targetType = toNumber(d.targetType ?? d.target_type);
    const errorCode = toNumber(d.errorCode ?? d.error_code);

    // State derivation — phase-aware so a non-compliant health-script detection (exit 1)
    // does NOT show as a red Failed card. Detection / post-detection scripts use the exit
    // code as a compliance verdict, not a crash signal: exit 0 = compliant, exit non-zero
    // = non-compliant (the script ran fine, it's reporting state). Only the remediation
    // phase + platform scripts treat non-zero exit as a real failure. The defensive check
    // also handles legacy emitters that send script_completed for actual failures.
    const isHealthComplianceReport = scriptType === "remediation"
      && (scriptPart === "detection" || scriptPart === "post-detection");
    const isFailureSignal = evt.eventType === "script_failed"
      || (!isHealthComplianceReport && exitCode != null && exitCode !== 0)
      || d.result === "Failed";

    items.push({
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
      stdout: d.stdout,
      stderr: d.stderr,
      state: isFailureSignal ? "Failed" : "Success",
      timestamp: evt.timestamp,
      firstSeenIndex: items.length,
      bootstrapVersion: scriptType === "platform" ? extractBootstrapVersion(d.stdout) : null,
    });
  }

  const seenRunning = new Set<string>();
  for (let idx = 0; idx < sorted.length; idx++) {
    const evt = sorted[idx];
    if (evt.eventType !== "script_started") continue;
    const d = evt.data;
    if (!d) continue;

    const policyId = d.policyId ?? d.policy_id ?? "";
    const scriptType = d.scriptType ?? d.script_type ?? "platform";
    if (!policyId) continue;

    // Suppress this placeholder if a final event for the same (policyId, scriptType)
    // exists with a timestamp at-or-after this started timestamp. That covers two
    // cases: (a) the normal flow where the final lands after the started event;
    // (b) a re-run scenario where an *earlier* started signal already has a matching
    // final, but a *later* started should still surface as Running until ITS final
    // arrives.
    const startedTs = new Date(evt.timestamp).getTime();
    const finals = finalTimestampsByPolicy.get(`${policyId}-${scriptType}`);
    if (finals && finals.some(ts => ts >= startedTs)) continue;

    // Per-timestamp key so two started-only signals for the same policy in different
    // runs both render placeholders.
    const key = `${policyId}-${scriptType}-_running-${evt.timestamp}`;
    if (seenRunning.has(key)) continue;
    seenRunning.add(key);

    items.push({
      policyId,
      scriptType,
      state: "Running",
      timestamp: evt.timestamp,
      firstSeenIndex: items.length,
      bootstrapVersion: null,
    });
  }

  return items;
}
