"use client";

import { useMemo, useState } from "react";
import type { SignalRecord } from "../types";

interface SignalStreamProps {
  signals: SignalRecord[];
  count: number;
  truncated: boolean;
  loading: boolean;
  error: string | null;
}

/**
 * Read-only chronological list of every DecisionSignal the agent produced for
 * this session. The Inspector uses this to answer "what raw evidence did the
 * reducer actually see, and in what order?" — the question the user wants for
 * 2-stage WhiteGlove modelling.
 *
 * Filtering by Kind / SourceOrigin is the cheapest way to make a 1000-signal
 * session navigable; the Evidence-Drawer (right side) shows the parsed
 * `payloadJson` blob so the user can inspect Evidence + Payload without
 * leaving the page.
 */
export function SignalStream({ signals, count, truncated, loading, error }: SignalStreamProps) {
  const [selectedOrdinal, setSelectedOrdinal] = useState<number | null>(null);
  const [kindFilter, setKindFilter] = useState<string>("");
  const [sourceFilter, setSourceFilter] = useState<string>("");

  const distinctKinds = useMemo(
    () => Array.from(new Set(signals.map((s) => s.kind))).sort(),
    [signals],
  );
  const distinctSources = useMemo(
    () => Array.from(new Set(signals.map((s) => s.sourceOrigin))).sort(),
    [signals],
  );

  const filtered = useMemo(
    () =>
      signals.filter(
        (s) =>
          (!kindFilter || s.kind === kindFilter) &&
          (!sourceFilter || s.sourceOrigin === sourceFilter),
      ),
    [signals, kindFilter, sourceFilter],
  );

  const selected = useMemo(
    () => signals.find((s) => s.sessionSignalOrdinal === selectedOrdinal) ?? null,
    [signals, selectedOrdinal],
  );

  if (loading) {
    return <div className="rounded border border-gray-200 bg-white p-8 text-center text-gray-500">Loading signals…</div>;
  }
  if (error) {
    return (
      <div className="rounded border border-red-200 bg-red-50 p-4 text-red-700">
        <strong>Failed to load signals:</strong> {error}
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_400px]">
      {/* List */}
      <div className="rounded border border-gray-200 bg-white">
        <div className="border-b border-gray-200 p-3 flex flex-wrap items-center gap-3 text-sm">
          <span className="text-gray-600">
            {filtered.length} of {count} signals
            {truncated && <span className="ml-1 text-amber-600">(truncated)</span>}
          </span>
          <select
            className="rounded border border-gray-300 px-2 py-1 text-xs"
            value={kindFilter}
            onChange={(e) => setKindFilter(e.target.value)}
          >
            <option value="">All kinds</option>
            {distinctKinds.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </select>
          <select
            className="rounded border border-gray-300 px-2 py-1 text-xs"
            value={sourceFilter}
            onChange={(e) => setSourceFilter(e.target.value)}
          >
            <option value="">All sources</option>
            {distinctSources.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </select>
        </div>
        <div className="max-h-[70vh] overflow-y-auto">
          <table className="w-full text-xs">
            <thead className="sticky top-0 bg-gray-50 text-left">
              <tr>
                <th className="px-3 py-2 font-medium text-gray-700">Ord</th>
                <th className="px-3 py-2 font-medium text-gray-700">Time (UTC)</th>
                <th className="px-3 py-2 font-medium text-gray-700">Kind</th>
                <th className="px-3 py-2 font-medium text-gray-700">Source</th>
              </tr>
            </thead>
            <tbody>
              {filtered.map((s) => (
                <tr
                  key={s.sessionSignalOrdinal}
                  className={`cursor-pointer border-t border-gray-100 hover:bg-blue-50 ${
                    s.sessionSignalOrdinal === selectedOrdinal ? "bg-blue-100" : ""
                  }`}
                  onClick={() => setSelectedOrdinal(s.sessionSignalOrdinal)}
                >
                  <td className="px-3 py-1.5 font-mono text-gray-500">{s.sessionSignalOrdinal}</td>
                  <td className="px-3 py-1.5 font-mono">{formatTime(s.occurredAtUtc)}</td>
                  <td className="px-3 py-1.5 font-medium">{s.kind}</td>
                  <td className="px-3 py-1.5 text-gray-600">{s.sourceOrigin}</td>
                </tr>
              ))}
              {filtered.length === 0 && (
                <tr>
                  <td colSpan={4} className="px-3 py-8 text-center text-gray-500">
                    No signals match the current filters.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* Drawer */}
      <EvidenceDrawer signal={selected} />
    </div>
  );
}

function EvidenceDrawer({ signal }: { signal: SignalRecord | null }) {
  if (!signal) {
    return (
      <div className="rounded border border-gray-200 bg-gray-50 p-4 text-sm text-gray-500">
        Click a signal to inspect its evidence + payload.
      </div>
    );
  }

  const parsed = tryParseJson(signal.payloadJson);
  return (
    <div className="rounded border border-gray-200 bg-white">
      <div className="border-b border-gray-200 p-3">
        <div className="text-sm font-semibold">{signal.kind}</div>
        <div className="text-xs text-gray-500">
          ord={signal.sessionSignalOrdinal} · trace={signal.sessionTraceOrdinal} ·{" "}
          {formatTime(signal.occurredAtUtc)} · {signal.sourceOrigin}
        </div>
      </div>
      <div className="max-h-[68vh] overflow-y-auto p-3">
        <pre className="whitespace-pre-wrap break-all font-mono text-xs text-gray-800">
          {parsed
            ? JSON.stringify(parsed, null, 2)
            : signal.payloadJson || "(empty payload)"}
        </pre>
      </div>
    </div>
  );
}

function formatTime(iso: string): string {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toISOString().substring(11, 23); // HH:MM:SS.mmm
}

function tryParseJson(raw: string): unknown | null {
  if (!raw) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}
