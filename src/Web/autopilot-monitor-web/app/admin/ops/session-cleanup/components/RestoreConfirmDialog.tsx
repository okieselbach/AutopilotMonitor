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

export function RestoreConfirmDialog({
  tenantId,
  sessionId,
  manifestId,
  onClose,
  onRestored,
  getAccessToken,
}: RestoreConfirmDialogProps) {
  const [reason, setReason] = useState("");
  const [dryRun, setDryRun] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<{ outcome: string; mode: string | null; dryRun: boolean } | null>(null);

  const handleSubmit = async () => {
    setSubmitting(true);
    setError(null);
    setOutcome(null);
    try {
      const resp = await authenticatedFetch(
        api.sessionDeletions.restore(sessionId),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          // Codex F1: explicit tenantId is mandatory for cross-tenant GA restores.
          // Codex follow-up: backend selects the restore mode automatically based on the
          // cascade state (Full / Partial); operators cannot — and must not — force a Full
          // restore against a Poisoned session or vice versa. The reason is persisted into
          // the deletion_restored audit row as `details.reason`.
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
      setOutcome({
        outcome: body?.outcome ?? "Completed",
        mode: typeof body?.mode === "string" ? body.mode : null,
        dryRun,
      });
      if (!dryRun) {
        // Real restore done — let the parent refresh its list.
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

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50 p-4"
      onClick={onClose}
    >
      <div
        className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="border-b border-gray-200 dark:border-gray-700 px-6 py-4 flex items-center gap-3">
          <div className="w-10 h-10 bg-amber-100 dark:bg-amber-900/30 rounded-full flex items-center justify-center">
            <svg className="w-5 h-5 text-amber-600 dark:text-amber-300" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v6h6M20 20v-6h-6M3 11a8 8 0 0114-5l3 3M21 13a8 8 0 01-14 5l-3-3" />
            </svg>
          </div>
          <div>
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Restore session from snapshot</h2>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5 font-mono">{tenantId} / {sessionId}</p>
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
              Forcing the wrong mode would corrupt state, so the choice is not exposed. Run with <strong>dry-run</strong> first to confirm the predicted mode.
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
              className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded text-sm text-gray-900 dark:text-white bg-white dark:bg-gray-900 focus:outline-none focus:ring-2 focus:ring-blue-500"
            />
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Stored on the <code className="bg-gray-100 dark:bg-gray-700 px-1 rounded">deletion_restored</code> audit row.
            </p>
          </div>

          <label className="flex items-center gap-2 cursor-pointer p-2 border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-900/20 rounded">
            <input
              type="checkbox"
              checked={dryRun}
              onChange={(e) => setDryRun(e.target.checked)}
              className="w-4 h-4"
            />
            <span className="text-sm text-amber-900 dark:text-amber-100">
              <strong>Dry run</strong> — verify the snapshot can be replayed without writing anything.
              Recommended for the first attempt.
            </span>
          </label>

          {outcome && (
            <div className="p-3 bg-green-50 dark:bg-green-900/30 border border-green-200 dark:border-green-700 rounded text-sm text-green-800 dark:text-green-200">
              Outcome: <strong>{outcome.outcome}</strong>
              {outcome.mode && (
                <> · Mode (auto-selected): <strong className="capitalize">{outcome.mode}</strong></>
              )}
              {outcome.dryRun ? " · dry-run — no rows written" : ""}
            </div>
          )}
          {error && (
            <div className="p-3 bg-red-50 dark:bg-red-900/30 border border-red-200 dark:border-red-700 rounded text-sm text-red-700 dark:text-red-200">
              {error}
            </div>
          )}
        </div>

        <div className="bg-gray-50 dark:bg-gray-900 px-6 py-3 border-t border-gray-200 dark:border-gray-700 flex justify-end gap-2 rounded-b-lg">
          <button
            onClick={onClose}
            disabled={submitting}
            className="px-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded text-gray-700 dark:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50"
          >
            Close
          </button>
          <button
            onClick={handleSubmit}
            disabled={submitting}
            className={`px-3 py-2 text-sm text-white rounded disabled:opacity-50 ${
              dryRun ? "bg-blue-600 hover:bg-blue-700" : "bg-amber-600 hover:bg-amber-700"
            }`}
          >
            {submitting ? "Running…" : dryRun ? "Run dry-run" : "Restore"}
          </button>
        </div>
      </div>
    </div>
  );
}
