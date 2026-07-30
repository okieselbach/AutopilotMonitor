"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface RestoreConfirmDialogProps {
  tenantId: string;
  sessionId: string;
  manifestId: string;
  onClose: () => void;
  onRestored: () => void;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}

interface RestoreResultView {
  outcome: string;
  mode: string | null;
  message: string | null;
  currentState: string | null;
  pendingManifestId: string | null;
  rowsRestoredByTable: Record<string, number>;
  rowsSkippedByTable: Record<string, number>;
  wouldRestoreByTable: Record<string, number>;
  inventoryReIncrements: number;
  durationMs: number;
}

export function RestoreConfirmDialog({
  tenantId,
  sessionId,
  manifestId,
  onClose,
  onRestored,
  getAccessToken,
}: RestoreConfirmDialogProps) {
  const [reason, setReason] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Two-slot result store so the operator can compare predicted (dry-run) vs actual (real-run)
  // in the same modal. Clearing both via "Start over" lets them re-run from scratch.
  const [dryRunResult, setDryRunResult] = useState<RestoreResultView | null>(null);
  const [realRunResult, setRealRunResult] = useState<RestoreResultView | null>(null);

  const submit = async (dryRun: boolean) => {
    setSubmitting(true);
    setError(null);
    try {
      const resp = await authenticatedFetch(
        api.sessionDeletions.restore(sessionId),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            tenantId,
            manifestId,
            reason: reason.trim() || null,
            dryRun,
          }),
        },
      );
      const body = await resp.json().catch(() => ({}));
      if (!resp.ok) {
        throw new Error(body?.message ?? `Restore failed: HTTP ${resp.status}`);
      }
      const view = parseRestoreResult(body);
      if (dryRun) {
        setDryRunResult(view);
      } else {
        setRealRunResult(view);
        onRestored();
      }
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setSubmitting(false);
    }
  };

  const handleReset = () => {
    setDryRunResult(null);
    setRealRunResult(null);
    setError(null);
  };

  const hasResults = dryRunResult != null || realRunResult != null;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50 p-4"
      onClick={onClose}
    >
      <div
        className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-2xl w-full max-h-[85vh] overflow-y-auto"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="sticky top-0 bg-white dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700 px-6 py-4 flex items-center gap-3 z-10">
          <div className="w-10 h-10 bg-amber-100 dark:bg-amber-900/30 rounded-full flex items-center justify-center shrink-0">
            <svg className="w-5 h-5 text-amber-600 dark:text-amber-300" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v6h6M20 20v-6h-6M3 11a8 8 0 0114-5l3 3M21 13a8 8 0 01-14 5l-3-3" />
            </svg>
          </div>
          <div className="min-w-0">
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Restore session from snapshot</h2>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5 font-mono truncate">{tenantId} / {sessionId}</p>
          </div>
        </div>

        <div className="px-6 py-4 space-y-4">
          <div className="p-3 border border-gray-200 dark:border-gray-700 bg-gray-50 dark:bg-gray-900/40 rounded text-sm text-gray-700 dark:text-gray-200">
            <p>
              <strong>Mode is selected automatically</strong> based on the cascade state at execution time:
            </p>
            <ul className="mt-1 pl-5 list-disc text-xs text-gray-600 dark:text-gray-400 space-y-0.5">
              <li><strong>Partial</strong> — Poisoned cascade: replays only the rows the worker had already removed and re-increments inventory counters.</li>
              <li><strong>Full</strong> — Completed cascade (Sessions row gone): inserts every row from the snapshot and resets <code className="bg-gray-200 dark:bg-gray-700 px-1 rounded">DeletionState=None</code>.</li>
            </ul>
            <p className="mt-2 text-xs text-gray-500 dark:text-gray-400">
              Forcing the wrong mode would corrupt state, so the choice is not exposed. Run with <strong>dry-run</strong> first to confirm the predicted mode + row counts.
            </p>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-1">
              Reason (audit trail)
            </label>
            <input
              type="text"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              placeholder="e.g. customer support — accidental delete"
              maxLength={200}
              disabled={submitting}
              className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded text-sm text-gray-900 dark:text-white bg-white dark:bg-gray-900 focus:outline-none focus:ring-2 focus:ring-green-500 disabled:opacity-60"
            />
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Stored on the <code className="bg-gray-100 dark:bg-gray-700 px-1 rounded">deletion_restored</code> audit row when the real restore runs. Dry-runs are intentionally audit-free.
            </p>
          </div>

          {dryRunResult && <ResultSection title="Dry-run preview" tone="blue" result={dryRunResult} kind="dryRun" />}
          {realRunResult && <ResultSection title="Real restore" tone="green" result={realRunResult} kind="real" />}

          {error && (
            <div className="p-3 bg-red-50 dark:bg-red-900/30 border border-red-200 dark:border-red-700 rounded text-sm text-red-700 dark:text-red-200">
              {error}
            </div>
          )}
        </div>

        <div className="sticky bottom-0 bg-gray-50 dark:bg-gray-900 px-6 py-3 border-t border-gray-200 dark:border-gray-700 flex flex-wrap justify-end gap-2 rounded-b-lg">
          {hasResults && (
            <button
              onClick={handleReset}
              disabled={submitting}
              className="px-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded text-gray-700 dark:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50"
            >
              Start over
            </button>
          )}
          <button
            onClick={onClose}
            disabled={submitting}
            className="px-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded text-gray-700 dark:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50"
          >
            Close
          </button>
          {realRunResult == null && (
            <button
              onClick={() => submit(true)}
              disabled={submitting}
              className="px-3 py-2 text-sm bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50"
            >
              {submitting ? "Running…" : dryRunResult ? "Run dry-run again" : "Run dry-run"}
            </button>
          )}
          {realRunResult == null && (
            <button
              onClick={() => submit(false)}
              disabled={submitting}
              className="px-3 py-2 text-sm bg-amber-600 text-white rounded hover:bg-amber-700 disabled:opacity-50"
              title={dryRunResult ? "Apply the restore (writes rows)" : "Skip dry-run and restore (not recommended)"}
            >
              {submitting ? "Running…" : dryRunResult ? "Apply restore" : "Skip dry-run · Restore now"}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}

function ResultSection({
  title,
  tone,
  result,
  kind,
}: {
  title: string;
  tone: "blue" | "green";
  result: RestoreResultView;
  kind: "dryRun" | "real";
}) {
  const toneClasses = tone === "blue"
    ? "bg-blue-50 dark:bg-blue-900/20 border-blue-200 dark:border-blue-700"
    : "bg-green-50 dark:bg-green-900/20 border-green-200 dark:border-green-700";
  const titleColor = tone === "blue"
    ? "text-blue-900 dark:text-blue-100"
    : "text-green-900 dark:text-green-100";

  const tableMap = kind === "dryRun" ? result.wouldRestoreByTable : result.rowsRestoredByTable;
  const tableLabel = kind === "dryRun" ? "Would restore by table" : "Rows restored by table";
  const total = Object.values(tableMap).reduce((sum, n) => sum + (Number.isFinite(n) ? n : 0), 0);
  const skippedTotal = Object.values(result.rowsSkippedByTable).reduce((sum, n) => sum + (Number.isFinite(n) ? n : 0), 0);

  return (
    <div className={`border rounded p-3 text-sm space-y-2 ${toneClasses}`}>
      <div className="flex items-baseline justify-between gap-2 flex-wrap">
        <h3 className={`text-sm font-semibold ${titleColor}`}>{title}</h3>
        <span className="text-xs font-mono text-gray-600 dark:text-gray-300">
          Outcome: <strong>{result.outcome}</strong>
          {result.mode && <> · Mode: <strong className="capitalize">{result.mode}</strong></>}
          <> · {result.durationMs} ms</>
        </span>
      </div>

      {result.message && (
        <p className="text-xs text-gray-700 dark:text-gray-300 italic">{result.message}</p>
      )}

      {(result.currentState || result.pendingManifestId) && (
        <div className="grid grid-cols-2 gap-2 text-xs text-gray-600 dark:text-gray-400">
          {result.currentState && (
            <span>Current state: <code className="bg-white dark:bg-gray-900 px-1 rounded">{result.currentState}</code></span>
          )}
          {result.pendingManifestId && (
            <span className="truncate">Pending: <code className="bg-white dark:bg-gray-900 px-1 rounded font-mono">{result.pendingManifestId}</code></span>
          )}
        </div>
      )}

      <div>
        <p className="text-xs font-medium text-gray-700 dark:text-gray-200">
          {tableLabel} ({total} {total === 1 ? "row" : "rows"} total{skippedTotal > 0 ? `, ${skippedTotal} skipped` : ""})
        </p>
        {Object.keys(tableMap).length > 0 ? (
          <table className="w-full mt-1 text-xs">
            <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
              {Object.entries(tableMap)
                .sort(([, a], [, b]) => b - a)
                .map(([table, count]) => (
                  <tr key={table}>
                    <td className="py-0.5 font-mono text-gray-700 dark:text-gray-300">{table}</td>
                    <td className="py-0.5 text-right text-gray-800 dark:text-gray-200">{count}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        ) : (
          <p className="text-xs text-gray-500 dark:text-gray-400 italic">— no tables touched</p>
        )}
      </div>

      {result.inventoryReIncrements > 0 && (
        <p className="text-xs text-gray-600 dark:text-gray-400">
          {kind === "dryRun" ? "Would re-increment" : "Re-incremented"} <strong>{result.inventoryReIncrements}</strong> software-inventory counter{result.inventoryReIncrements === 1 ? "" : "s"}.
        </p>
      )}
    </div>
  );
}

function parseRestoreResult(body: Record<string, unknown>): RestoreResultView {
  return {
    outcome: typeof body.outcome === "string" ? body.outcome : "Unknown",
    mode: typeof body.mode === "string" ? body.mode : null,
    message: typeof body.message === "string" ? body.message : null,
    currentState: typeof body.currentState === "string" ? body.currentState : null,
    pendingManifestId: typeof body.pendingManifestId === "string" ? body.pendingManifestId : null,
    rowsRestoredByTable: isStringNumberMap(body.rowsRestoredByTable) ? body.rowsRestoredByTable : {},
    rowsSkippedByTable: isStringNumberMap(body.rowsSkippedByTable) ? body.rowsSkippedByTable : {},
    wouldRestoreByTable: isStringNumberMap(body.wouldRestoreByTable) ? body.wouldRestoreByTable : {},
    inventoryReIncrements: typeof body.inventoryReIncrements === "number" ? body.inventoryReIncrements : 0,
    durationMs: typeof body.durationMs === "number" ? body.durationMs : 0,
  };
}

function isStringNumberMap(value: unknown): value is Record<string, number> {
  return (
    typeof value === "object"
    && value !== null
    && !Array.isArray(value)
    && Object.values(value).every((v) => typeof v === "number")
  );
}
