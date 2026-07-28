"use client";

import { useMemo, useState } from "react";
import type { EnrollmentEvent } from "@/types";
import {
  DecisionStateCard,
  type DecisionStateSnapshot,
} from "./DecisionStateCard";

interface LifecycleAnchorsProps {
  anchors: EnrollmentEvent[];
  totalEvents: number;
  loading: boolean;
  error: string | null;
}

/**
 * "Anchors" tab in the Inspector — renders every event the agent emitted with a
 * `data.decisionState` payload (Plan §A — Edge-Triggered State Snapshots,
 * 2026-05-03). One row per anchor, oldest-first; expanding a row reveals the
 * post-reduce DecisionState as the agent saw it at emit time.
 *
 * Special-case: Death-Rattle (`prior_run_died_with_state`, Plan §B) carries
 * both `data.priorState` (the dying run's last persisted snapshot) AND
 * `data.decisionState` (the fresh run's reconstructed engine state) — render
 * them side-by-side so the operator can read the recovery delta directly.
 *
 * The Death-Rattle pairing is the load-bearing diagnostic: if priorState shows
 * the dying run was waiting on Hello while decisionState shows the fresh run
 * already resolved Hello via SignalLog tail-replay, you have direct evidence
 * that recovery did its job. If priorState shows AwaitingDesktop and
 * decisionState also shows AwaitingDesktop after recovery, the death lost no
 * progress.
 */
export function LifecycleAnchors({ anchors, totalEvents, loading, error }: LifecycleAnchorsProps) {
  const [filter, setFilter] = useState<string>("");

  const distinctEventTypes = useMemo(
    () => Array.from(new Set(anchors.map((a) => a.eventType))).sort(),
    [anchors],
  );

  const filtered = useMemo(
    () => (!filter ? anchors : anchors.filter((a) => a.eventType === filter)),
    [anchors, filter],
  );

  if (loading) {
    return (
      <div className="rounded border border-gray-200 bg-white p-8 text-center text-gray-500">
        Loading lifecycle anchors…
      </div>
    );
  }
  if (error) {
    return (
      <div className="rounded border border-red-200 bg-red-50 p-4 text-red-700">
        <strong>Failed to load anchors:</strong> {error}
      </div>
    );
  }

  if (anchors.length === 0) {
    return (
      <div className="rounded border border-gray-200 bg-gray-50 p-6 text-sm text-gray-600">
        <div className="font-semibold">No lifecycle-anchor snapshots in this session.</div>
        <div className="mt-1 text-gray-500">
          Edge-Triggered State Snapshots ship with agent versions that carry the Plan §A
          enrichment (post-2026-05-03). Older agents emit the same events but without the
          embedded <code className="font-mono">decisionState</code>. {totalEvents} events were
          fetched in total.
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3 text-sm">
        <span className="text-gray-600">
          {filtered.length} of {anchors.length} anchor{anchors.length === 1 ? "" : "s"}
        </span>
        <select
          className="rounded border border-gray-300 px-2 py-1 text-xs"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        >
          <option value="">All event types</option>
          {distinctEventTypes.map((t) => (
            <option key={t} value={t}>
              {t}
            </option>
          ))}
        </select>
      </div>

      <div className="space-y-3">
        {filtered.map((anchor) => (
          <AnchorRow key={anchor.eventId} anchor={anchor} />
        ))}
        {filtered.length === 0 && (
          <div className="rounded border border-gray-200 bg-gray-50 p-4 text-sm text-gray-500">
            No anchors match the current filter.
          </div>
        )}
      </div>
    </div>
  );
}

function AnchorRow({ anchor }: { anchor: EnrollmentEvent }) {
  const decisionState = anchor.data?.decisionState as DecisionStateSnapshot | undefined;
  const priorState = anchor.data?.priorState as DecisionStateSnapshot | undefined;
  const isDeathRattle = anchor.eventType === "prior_run_died_with_state";

  return (
    <div className="rounded border border-gray-200 bg-white">
      <div className="border-b border-gray-200 bg-gray-50 px-3 py-2">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <div className="flex items-baseline gap-2">
            <code className="font-mono text-sm font-semibold text-gray-900">
              {anchor.eventType}
            </code>
            <span className="font-mono text-xs text-gray-500">
              seq={anchor.sequence ?? "—"}
            </span>
            {isDeathRattle && (
              <span className="inline-flex items-center rounded bg-rose-100 px-1.5 py-0.5 text-[11px] font-semibold text-rose-800">
                Death-Rattle
              </span>
            )}
          </div>
          <span className="font-mono text-xs text-gray-500">
            {formatTime(anchor.timestamp)}
          </span>
        </div>
        {anchor.message && (
          <div className="mt-1 text-xs text-gray-700">{anchor.message}</div>
        )}
        {isDeathRattle && (
          <DeathRattleMetadata data={anchor.data ?? {}} />
        )}
      </div>

      <div className="space-y-2 p-3">
        {isDeathRattle && priorState ? (
          <>
            <DecisionStateCard
              snapshot={priorState}
              title="Prior run state (at death)"
              defaultExpanded={true}
            />
            {decisionState && (
              <DecisionStateCard
                snapshot={decisionState}
                title="Fresh run state (post-recovery, this emit)"
                defaultExpanded={true}
              />
            )}
          </>
        ) : decisionState ? (
          <DecisionStateCard snapshot={decisionState} />
        ) : (
          <div className="text-xs italic text-gray-500">
            (anchor event has no decisionState payload)
          </div>
        )}
      </div>
    </div>
  );
}

function DeathRattleMetadata({ data }: { data: Record<string, unknown> }) {
  const exitType = typeof data.previousExitType === "string" ? data.previousExitType : null;
  const lastBoot = typeof data.lastBootUtc === "string" && data.lastBootUtc.length > 0 ? data.lastBootUtc : null;
  const crashException = typeof data.previousCrashException === "string" ? data.previousCrashException : null;

  if (!exitType && !lastBoot && !crashException) return null;

  return (
    <div className="mt-1.5 grid grid-cols-1 gap-x-4 gap-y-0.5 text-[11px] sm:grid-cols-2">
      {exitType && (
        <div>
          <span className="text-gray-500">previousExitType:</span>{" "}
          <code className="font-mono text-rose-800">{exitType}</code>
        </div>
      )}
      {crashException && (
        <div>
          <span className="text-gray-500">crashException:</span>{" "}
          <code className="font-mono text-rose-800">{crashException}</code>
        </div>
      )}
      {lastBoot && (
        <div className="sm:col-span-2">
          <span className="text-gray-500">lastBootUtc:</span>{" "}
          <code className="font-mono text-gray-800">{lastBoot}</code>
        </div>
      )}
    </div>
  );
}

function formatTime(iso: string): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toISOString().substring(11, 23); // HH:MM:SS.mmm
}
