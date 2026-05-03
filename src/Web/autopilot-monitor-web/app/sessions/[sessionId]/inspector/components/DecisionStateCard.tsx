"use client";

import { useState } from "react";

/**
 * Renders one DecisionStateSnapshotBuilder output (Plan §A schema
 * `decision-state-snapshot-v1`, 2026-05-03). Tolerates legacy / unknown fields
 * — anything outside the known top-level allowlist drops into the "Other"
 * collapsible at the bottom rather than throwing.
 *
 * The card is intentionally schema-shape-only — the LifecycleAnchors parent
 * layer adds the per-event header (timestamp, eventType, message) and any
 * special-case framing like the Death-Rattle dual snapshot.
 */

export interface DecisionStateSnapshot {
  schemaVersion?: string;
  stepIndex?: number;
  lastAppliedSignalOrdinal?: number;
  stage?: string;
  outcome?: string | null;
  facts?: Record<string, FactValue | null>;
  scenario?: ScenarioBlock;
  activeDeadlines?: ActiveDeadline[];
  // Tolerate forwards-compat: extra top-level keys land here without breaking render.
  [key: string]: unknown;
}

export interface FactValue {
  value: unknown;
  ordinal: number;
}

export interface ScenarioBlock {
  mode?: string;
  joinMode?: string;
  espConfig?: string;
  preProvisioningSide?: string;
  confidence?: string;
  evidenceOrdinal?: number | null;
  reason?: string | null;
  [key: string]: unknown;
}

export interface ActiveDeadline {
  name?: string;
  dueAtUtc?: string;
}

interface DecisionStateCardProps {
  snapshot: DecisionStateSnapshot;
  /** Optional title prefix — e.g. "Snapshot at desktop_arrived" or "Prior run state". */
  title?: string;
  /** When false, render facts/scenario/deadlines collapsed by default (used in dual-snapshot Death-Rattle). */
  defaultExpanded?: boolean;
}

const KNOWN_TOP_LEVEL = new Set([
  "schemaVersion",
  "stepIndex",
  "lastAppliedSignalOrdinal",
  "stage",
  "outcome",
  "facts",
  "scenario",
  "activeDeadlines",
]);

export function DecisionStateCard({
  snapshot,
  title,
  defaultExpanded = true,
}: DecisionStateCardProps) {
  const [expanded, setExpanded] = useState(defaultExpanded);

  const facts = snapshot.facts ?? {};
  const setFacts: [string, FactValue][] = [];
  const nullFacts: string[] = [];
  Object.keys(facts)
    .sort()
    .forEach((k) => {
      const v = facts[k];
      if (v === null || v === undefined) nullFacts.push(k);
      else setFacts.push([k, v as FactValue]);
    });

  const scenario = snapshot.scenario;
  const deadlines = snapshot.activeDeadlines ?? [];
  const otherKeys = Object.keys(snapshot).filter((k) => !KNOWN_TOP_LEVEL.has(k));

  return (
    <div className="rounded border border-purple-200 bg-purple-50/40">
      <button
        type="button"
        className="flex w-full items-center justify-between px-3 py-2 text-left"
        onClick={() => setExpanded((e) => !e)}
        aria-expanded={expanded}
      >
        <span className="flex flex-col gap-0.5">
          <span className="text-sm font-semibold text-purple-900">
            {title ?? "DecisionState snapshot"}
          </span>
          <span className="text-xs text-purple-700">
            stage=<code className="font-mono">{snapshot.stage ?? "—"}</code>
            {" · "}stepIndex=<code className="font-mono">{snapshot.stepIndex ?? "—"}</code>
            {" · "}lastSignal=<code className="font-mono">{snapshot.lastAppliedSignalOrdinal ?? "—"}</code>
            {snapshot.outcome ? <> · outcome=<code className="font-mono">{snapshot.outcome}</code></> : null}
          </span>
        </span>
        <svg
          className={`h-4 w-4 text-purple-700 transition-transform ${expanded ? "rotate-180" : ""}`}
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {expanded && (
        <div className="space-y-3 border-t border-purple-200 px-3 py-3">
          {scenario && <ScenarioSection scenario={scenario} />}

          {(setFacts.length > 0 || nullFacts.length > 0) && (
            <FactsSection setFacts={setFacts} nullFacts={nullFacts} />
          )}

          <DeadlinesSection deadlines={deadlines} />

          {otherKeys.length > 0 && <OtherFieldsSection snapshot={snapshot} keys={otherKeys} />}

          {snapshot.schemaVersion && (
            <div className="pt-1 text-[11px] text-purple-700">
              schema <code className="font-mono">{snapshot.schemaVersion}</code>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function ScenarioSection({ scenario }: { scenario: ScenarioBlock }) {
  const knownKeys = ["mode", "joinMode", "espConfig", "preProvisioningSide", "confidence", "evidenceOrdinal", "reason"];
  return (
    <div>
      <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-purple-800">Scenario</div>
      <div className="grid grid-cols-1 gap-x-4 gap-y-1 sm:grid-cols-2">
        {knownKeys.map((k) => {
          const v = scenario[k];
          if (v === null || v === undefined) return null;
          return (
            <div key={k} className="text-xs">
              <span className="text-purple-700">{k}:</span>{" "}
              <code className="font-mono text-purple-900">{String(v)}</code>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function FactsSection({
  setFacts,
  nullFacts,
}: {
  setFacts: [string, FactValue][];
  nullFacts: string[];
}) {
  const [showNulls, setShowNulls] = useState(false);
  return (
    <div>
      <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-purple-800">
        Facts ({setFacts.length} set{nullFacts.length > 0 && `, ${nullFacts.length} null`})
      </div>
      {setFacts.length === 0 && nullFacts.length === 0 && (
        <div className="text-xs italic text-gray-500">no facts on this snapshot</div>
      )}
      {setFacts.length > 0 && (
        <table className="w-full text-xs">
          <thead className="text-purple-700">
            <tr>
              <th className="py-1 pr-2 text-left font-medium">Fact</th>
              <th className="py-1 pr-2 text-left font-medium">Value</th>
              <th className="py-1 pr-2 text-left font-medium">Ord</th>
            </tr>
          </thead>
          <tbody>
            {setFacts.map(([name, fact]) => (
              <tr key={name} className="border-t border-purple-100">
                <td className="py-1 pr-2 font-mono text-purple-900">{name}</td>
                <td className="py-1 pr-2 break-all font-mono text-gray-800">{formatFactValue(fact.value)}</td>
                <td className="py-1 pr-2 font-mono text-gray-500">{fact.ordinal}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {nullFacts.length > 0 && (
        <div className="mt-1">
          <button
            type="button"
            className="text-[11px] text-purple-700 hover:underline"
            onClick={() => setShowNulls((s) => !s)}
          >
            {showNulls ? "Hide" : "Show"} {nullFacts.length} null fact{nullFacts.length === 1 ? "" : "s"}
          </button>
          {showNulls && (
            <div className="mt-1 flex flex-wrap gap-1">
              {nullFacts.map((n) => (
                <span
                  key={n}
                  className="inline-flex items-center rounded border border-gray-300 bg-white px-1.5 py-0.5 font-mono text-[11px] text-gray-500"
                >
                  {n}
                </span>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function DeadlinesSection({ deadlines }: { deadlines: ActiveDeadline[] }) {
  return (
    <div>
      <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-purple-800">
        Active deadlines ({deadlines.length})
      </div>
      {deadlines.length === 0 ? (
        <div className="text-xs italic text-gray-500">none</div>
      ) : (
        <ul className="space-y-0.5 text-xs">
          {deadlines.map((d, i) => (
            <li key={`${d.name}-${i}`} className="font-mono text-purple-900">
              {d.name ?? "?"}
              {d.dueAtUtc && <span className="text-gray-500"> · due {d.dueAtUtc}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function OtherFieldsSection({ snapshot, keys }: { snapshot: DecisionStateSnapshot; keys: string[] }) {
  return (
    <div>
      <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-amber-800">
        Other fields (forwards-compat)
      </div>
      <pre className="overflow-x-auto rounded border border-amber-200 bg-amber-50/60 p-2 text-[11px]">
        {JSON.stringify(
          Object.fromEntries(keys.map((k) => [k, snapshot[k]])),
          null,
          2,
        )}
      </pre>
    </div>
  );
}

function formatFactValue(v: unknown): string {
  if (v === null || v === undefined) return "—";
  if (typeof v === "string" || typeof v === "number" || typeof v === "boolean") return String(v);
  try {
    return JSON.stringify(v);
  } catch {
    return String(v);
  }
}
