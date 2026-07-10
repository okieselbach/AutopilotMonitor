"use client";

import { useState } from "react";

/**
 * Renders the maintenance-built failure snapshot when present (Hybrid User-Driven
 * completion-gap fix, 2026-05-01). The backend writes the JSON only on the 5h-timeout
 * graduation path, so this block is hidden for healthy completions and for sessions
 * that predate the field. The shape is best-effort — unknown fields are tolerated;
 * legacy snapshots without `schemaVersion` simply render the fields they do carry.
 */
interface FailureSnapshot {
  schemaVersion?: number;
  generatedAtUtc?: string;
  eventCount?: number;
  lastEventAtUtc?: string;
  silenceMinutes?: number;
  lastEspPhase?: string | null;
  lastEspPhaseAtUtc?: string | null;
  desktopArrived?: boolean;
  desktopArrivedAtUtc?: string | null;
  helloPolicyDetected?: boolean;
  helloPolicyEnabled?: boolean | null;
  aadJoinState?: string;
  aadJoinStateAtUtc?: string | null;
  rebootObserved?: boolean;
  isHybridJoin?: boolean | null;
  enrollmentType?: string | null;
  lastNetworkState?: string | null;
  // Schema v3 (2026-07-10): evidence behind the "user completed setup" reconcile rule.
  helloResolved?: boolean;
  skipUserEsp?: boolean;
  realmJoinDetected?: boolean;
  realmJoinResolved?: boolean;
  missingSignals?: string[];
}

export default function FailureSnapshotBlock({
  failureSnapshotJson,
}: {
  failureSnapshotJson?: string;
}) {
  const [expanded, setExpanded] = useState(false);

  if (!failureSnapshotJson || failureSnapshotJson.trim().length === 0) return null;

  let snapshot: FailureSnapshot | null = null;
  try {
    snapshot = JSON.parse(failureSnapshotJson) as FailureSnapshot;
  } catch {
    // Invalid JSON — render the raw string in a debug fallback so operators can still
    // copy/paste it into a JSON formatter offline. No exception bubbles up to break
    // the surrounding card.
    return (
      <div className="mt-4 p-3 rounded-lg bg-rose-50 border border-rose-200 text-sm text-rose-800">
        <strong>Failure Snapshot (raw):</strong>
        <pre className="mt-2 text-xs font-mono whitespace-pre-wrap break-all">{failureSnapshotJson}</pre>
      </div>
    );
  }

  const missing = snapshot.missingSignals ?? [];

  return (
    <div className="mt-4 rounded-lg bg-rose-50 border border-rose-200 text-sm text-rose-900">
      <button
        type="button"
        className="w-full flex items-center justify-between px-3 py-2 text-left font-semibold"
        onClick={() => setExpanded((e) => !e)}
        aria-expanded={expanded}
      >
        <span className="flex items-center gap-2">
          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
          </svg>
          Failure Snapshot {snapshot.silenceMinutes !== undefined && <span className="font-normal">(agent went silent {snapshot.silenceMinutes} min before timeout)</span>}
        </span>
        <svg className={`w-4 h-4 transition-transform ${expanded ? "rotate-180" : ""}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>
      {expanded && (
        <div className="px-3 pb-3 pt-1 space-y-2">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-x-4 gap-y-1.5">
            <SnapshotItem label="Last ESP phase" value={snapshot.lastEspPhase ?? "—"} sub={snapshot.lastEspPhaseAtUtc ?? undefined} />
            <SnapshotItem label="Desktop arrived" value={fmtBool(snapshot.desktopArrived)} sub={snapshot.desktopArrivedAtUtc ?? undefined} />
            <SnapshotItem label="Hello policy" value={fmtHelloPolicy(snapshot)} />
            {snapshot.helloResolved !== undefined && (
              <SnapshotItem label="Hello completed" value={fmtBool(snapshot.helloResolved)} />
            )}
            {snapshot.skipUserEsp !== undefined && (
              <SnapshotItem label="User ESP" value={snapshot.skipUserEsp ? "skipped (profile)" : "required"} />
            )}
            {snapshot.realmJoinDetected !== undefined && (
              <SnapshotItem label="RealmJoin" value={fmtRealmJoin(snapshot)} />
            )}
            <SnapshotItem label="AAD join state" value={snapshot.aadJoinState ?? "unknown"} sub={snapshot.aadJoinStateAtUtc ?? undefined} />
            <SnapshotItem label="Reboot observed" value={fmtBool(snapshot.rebootObserved)} />
            <SnapshotItem label="Hybrid AAD join" value={snapshot.isHybridJoin === null || snapshot.isHybridJoin === undefined ? "unknown" : fmtBool(snapshot.isHybridJoin)} />
            <SnapshotItem label="Enrollment type" value={snapshot.enrollmentType ?? "unknown"} />
            <SnapshotItem label="Event count" value={snapshot.eventCount?.toString() ?? "—"} />
            {snapshot.lastNetworkState && (
              <SnapshotItem label="Last network state" value={snapshot.lastNetworkState} className="sm:col-span-2" />
            )}
          </div>
          {missing.length > 0 && (
            <div className="pt-2">
              <div className="text-xs font-semibold text-rose-800 mb-1">Missing canonical signals at timeout:</div>
              <div className="flex flex-wrap gap-1.5">
                {missing.map((sig) => (
                  <span key={sig} className="inline-flex items-center px-2 py-0.5 text-xs font-mono rounded bg-white border border-rose-300 text-rose-800">
                    {sig}
                  </span>
                ))}
              </div>
            </div>
          )}
          {snapshot.generatedAtUtc && (
            <div className="text-xs text-rose-700 pt-1">
              Snapshot generated {new Date(snapshot.generatedAtUtc).toLocaleString()}
              {snapshot.schemaVersion !== undefined && <> · schema v{snapshot.schemaVersion}</>}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function SnapshotItem({ label, value, sub, className }: { label: string; value: React.ReactNode; sub?: string; className?: string }) {
  return (
    <div className={className}>
      <div className="text-xs font-semibold text-rose-800">{label}</div>
      <div className="text-sm text-rose-900 break-words">{value}</div>
      {sub && <div className="text-[11px] text-rose-700 font-mono">{sub}</div>}
    </div>
  );
}

function fmtBool(v: boolean | undefined | null) {
  if (v === true) return "yes";
  if (v === false) return "no";
  return "unknown";
}

function fmtHelloPolicy(s: FailureSnapshot): string {
  if (!s.helloPolicyDetected) return "not detected";
  if (s.helloPolicyEnabled === true) return "detected (enabled)";
  if (s.helloPolicyEnabled === false) return "detected (disabled)";
  return "detected";
}

function fmtRealmJoin(s: FailureSnapshot): string {
  if (!s.realmJoinDetected) return "not detected";
  // "unresolved" = RealmJoin never reported CompletedFirstDeployment (phase 110) and the
  // 60-min gate timeout wasn't observed either — the deployment was still in flight when
  // the agent went silent.
  return s.realmJoinResolved ? "detected (resolved)" : "detected (unresolved)";
}
