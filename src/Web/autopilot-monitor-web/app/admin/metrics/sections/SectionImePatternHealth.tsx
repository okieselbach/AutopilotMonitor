"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { useAuth } from "../../../../contexts/AuthContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";
import {
  buildMatrix,
  countStates,
  formatRate,
  type CellState,
  type ImePatternHealthResponse,
  type MatrixRow,
} from "./imePatternHealthLogic";

const CELL_CLASS: Record<CellState, string> = {
  drift: "bg-red-100 text-red-800 font-semibold",
  silent: "bg-amber-100 text-amber-800 font-semibold",
  low: "bg-amber-50 text-amber-700",
  ok: "text-gray-700",
  few: "text-gray-400",
  none: "text-gray-300",
};

const CELL_LEGEND: Array<{ state: CellState; label: string }> = [
  { state: "drift", label: "drift flagged (ops event raised)" },
  { state: "silent", label: "expected pattern, zero hits" },
  { state: "low", label: "expected pattern, rate < half of baseline expectation" },
  { state: "few", label: "too few sessions to judge" },
];

export function SectionImePatternHealth() {
  const { getAccessToken } = useAuth();
  const [data, setData] = useState<ImePatternHealthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [includeDisabled, setIncludeDisabled] = useState(false);

  const fetchData = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await authenticatedFetch(api.metrics.globalImePatternHealth(), getAccessToken);
      if (res.status === 404) {
        // Deploy skew: backend without the route yet — reads as "no rows", never as an error.
        setData(null);
        return;
      }
      if (!res.ok) throw new Error(`IME pattern health: ${res.status}`);
      setData((await res.json()) as ImePatternHealthResponse);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired. Please refresh the page.");
      } else {
        setError(err instanceof Error ? err.message : "Failed to load IME pattern health");
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    // Indirect call: the fetch resolves asynchronously, so no setState runs inside the effect body.
    const run = async () => {
      await fetchData();
    };
    void run();
  }, [fetchData]);

  const rows: MatrixRow[] = data ? buildMatrix(data, includeDisabled) : [];
  const counts = countStates(rows);
  const reportingSessions = data ? data.versions.reduce((acc, v) => acc + v.sessions, 0) : 0;

  return (
    <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">IME Pattern Health</h2>
          <p className="text-sm text-gray-500">
            Which shipped IME log patterns still match on which IME version. Built from the agents&apos; session-end
            {" "}<span className="font-mono">ime_pattern_hits</span> histograms; only sessions that reached a terminal run report one.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <label className="flex items-center gap-2 text-sm text-gray-600">
            <input type="checkbox" checked={includeDisabled} onChange={(e) => setIncludeDisabled(e.target.checked)} />
            Show disabled / retired patterns
          </label>
          <button
            onClick={() => fetchData()}
            disabled={loading}
            className="px-3 py-1.5 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50"
          >
            Refresh
          </button>
        </div>
      </div>

      {error && (
        <div className="bg-red-50 border border-red-200 text-red-700 text-sm rounded-md px-4 py-3">{error}</div>
      )}

      {data && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          <Stat label="Baseline version" value={data.baselineVersion ?? "—"} hint={`version with most reporting sessions (≥ ${data.minBaselineSessions})`} />
          <Stat label="Reporting sessions" value={reportingSessions.toLocaleString()} hint={`${data.versions.length} IME versions with histograms`} />
          <Stat label="Drift alerts" value={data.alerts.length.toLocaleString()} hint="expected pattern never matched on a newer version" />
          <Stat label="Attention cells" value={(counts.drift + counts.silent + counts.low).toLocaleString()} hint={`${counts.drift} drift · ${counts.silent} silent · ${counts.low} low`} />
        </div>
      )}

      {data && data.alerts.length > 0 && (
        <div className="bg-white shadow rounded-lg p-6">
          <h3 className="text-sm font-semibold text-gray-900">Open drift alerts</h3>
          <p className="text-xs text-gray-500 mb-3">
            One <span className="font-mono">ImePatternDriftSuspected</span> ops event per version × pattern. Next step: pull a diagnostics package of a session on that IME version,
            validate the pattern against the real log, compare with the IME decompile, fix the pattern under{" "}
            <Link href="/ime-log-patterns" className="text-green-700 hover:text-green-800">IME Log Patterns</Link>.
          </p>
          <ul className="divide-y divide-gray-100">
            {data.alerts.map((a) => (
              <li key={`${a.version}|${a.patternId}`} className="py-1.5 text-xs flex flex-wrap items-center gap-x-3">
                <span className="font-mono font-semibold text-gray-900">{a.patternId}</span>
                <span className="text-gray-700">IME {a.version}</span>
                <span className="text-gray-500">0 / {a.sessions} sessions</span>
                <span className="text-gray-500">baseline {a.baselineVersion}: {formatRate(a.baselineRate)}</span>
                {a.flaggedAt && <span className="text-gray-400">{new Date(a.flaggedAt).toLocaleString()}</span>}
              </li>
            ))}
          </ul>
        </div>
      )}

      <div className="bg-white shadow rounded-lg p-6">
        {loading && !data ? (
          <p className="text-sm text-gray-500">Loading…</p>
        ) : !data || data.versions.length === 0 ? (
          <p className="text-sm text-gray-500">
            No histograms yet. Agents send <span className="font-mono">ime_pattern_hits</span> at session end once the release carrying it is deployed.
          </p>
        ) : (
          <>
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-gray-500 mb-3">
              {CELL_LEGEND.map((l) => (
                <span key={l.state} className="flex items-center gap-1">
                  <span className={`inline-block w-3 h-3 rounded-sm border border-gray-200 ${CELL_CLASS[l.state]}`} />
                  {l.label}
                </span>
              ))}
              <span>cell = share of reporting sessions in which the pattern matched</span>
            </div>
            <div className="overflow-x-auto">
              <table className="min-w-full text-xs">
                <thead>
                  <tr className="text-left text-gray-500 border-b border-gray-200">
                    <th className="py-1 pr-3 font-medium">Pattern</th>
                    <th className="py-1 pr-3 font-medium">Category</th>
                    <th className="py-1 pr-3 font-medium text-right">Baseline</th>
                    {data.versions.map((v) => (
                      <th key={v.version} className="py-1 px-2 font-medium text-right whitespace-nowrap" title={`${v.sessions} reporting sessions${v.fleetSessions ? ` of ${v.fleetSessions} fleet sessions` : ""}`}>
                        <span className="font-mono">{v.version}</span>
                        {v.version === data.baselineVersion && <span className="ml-1 text-gray-400">(base)</span>}
                        <div className="text-gray-400 font-normal">{v.sessions.toLocaleString()} s.</div>
                      </th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-100">
                  {rows.map((r) => (
                    <tr key={r.pattern.patternId} className={r.pattern.enabled ? "" : "text-gray-400"}>
                      <td className="py-0.5 pr-3 font-mono whitespace-nowrap">
                        {r.pattern.patternId}
                        {!r.pattern.enabled && <span className="ml-1 text-gray-400">(off)</span>}
                        {r.pattern.expected && <span className="ml-1 text-gray-400" title="expected: ≥ threshold on the baseline">★</span>}
                      </td>
                      <td className="py-0.5 pr-3 text-gray-500 whitespace-nowrap">{r.pattern.category ?? "—"}</td>
                      <td className="py-0.5 pr-3 text-right font-mono text-gray-500">{formatRate(r.pattern.baselineRate)}</td>
                      {r.cells.map((c, i) => (
                        <td
                          key={data.versions[i].version}
                          className={`py-0.5 px-2 text-right font-mono whitespace-nowrap ${CELL_CLASS[c.state]}`}
                          title={c.cell ? `${c.cell.sessionsWithHit} of ${c.cell.sessions} sessions · ${c.cell.hits.toLocaleString()} matches` : "no data"}
                        >
                          {c.cell ? formatRate(c.cell.rate) : "—"}
                        </td>
                      ))}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function Stat({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <div className="bg-white shadow rounded-lg p-4">
      <p className="text-xs font-medium text-gray-500 uppercase tracking-wider">{label}</p>
      <p className="mt-1 text-2xl font-semibold text-gray-900">{value}</p>
      <p className="text-xs text-gray-400">{hint}</p>
    </div>
  );
}
