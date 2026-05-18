"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import {
  api,
  type CustomsArchiveListRunsResponse,
  type CustomsArchiveRunSummary,
} from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../AdminConfigContext";
import { AdminNotifications } from "../AdminNotifications";
import { DeleteConfirmModal } from "./components/DeleteConfirmModal";

export default function CustomsArchivePage() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();
  const [filter, setFilter] = useState<string>("");
  const [runs, setRuns] = useState<CustomsArchiveRunSummary[]>([]);
  const [loading, setLoading] = useState<boolean>(false);
  const [deletingPk, setDeletingPk] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<CustomsArchiveRunSummary | null>(null);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const url = api.customsArchive.listRuns({ tenantId: filter.trim() || undefined });
      const res = await authenticatedFetch(url, getAccessToken);
      if (!res.ok) {
        throw new Error(`List runs failed: ${res.status} ${res.statusText}`);
      }
      const body = (await res.json()) as CustomsArchiveListRunsResponse;
      setRuns(body.runs ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError(err.message);
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setLoading(false);
    }
  }, [filter, getAccessToken, setError]);

  useEffect(() => {
    void load();
  }, [load]);

  const confirmDelete = useCallback(async () => {
    const run = pendingDelete;
    if (!run) return;
    try {
      setDeletingPk(run.partitionKey);
      const res = await authenticatedFetch(
        api.customsArchive.deleteRun(run.tenantId, run.historyRowKey),
        getAccessToken,
        { method: "DELETE" }
      );
      if (!res.ok) {
        throw new Error(`Delete run failed: ${res.status} ${res.statusText}`);
      }
      const body = await res.json();
      setSuccessMessage(`Deleted ${body.deleted ?? 0} archived rules.`);
      setPendingDelete(null);
      await load();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError(err.message);
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setDeletingPk(null);
    }
  }, [pendingDelete, getAccessToken, load, setError, setSuccessMessage]);

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white">Customs Archive</h1>
          <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
            Snapshots of each tenant&apos;s custom <strong>GatherRules</strong> / <strong>AnalyzeRules</strong> /{" "}
            <strong>ImeLogPatterns</strong> taken during offboarding (Phase 2.D-archive). Review the content,
            then delete entries selectively when no longer needed. No auto-cleanup.
          </p>
        </div>
      </header>

      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
        <AdminNotifications />

        <div className="flex flex-wrap items-center gap-2">
          <input
            type="text"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder="Filter by tenant ID (optional)"
            className="px-3 py-1.5 border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-800 text-sm text-gray-900 dark:text-white"
          />
          <button
            type="button"
            onClick={load}
            disabled={loading}
            className="px-3 py-1.5 bg-blue-600 hover:bg-blue-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
          >
            {loading ? "Loading…" : "Refresh"}
          </button>
          <span className="text-sm text-gray-500 dark:text-gray-400 ml-2">{runs.length} run(s)</span>
        </div>

        {runs.length === 0 && !loading && (
          <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6 text-sm text-gray-600 dark:text-gray-400">
            No archived offboarding runs found{filter ? ` for tenant ${filter}.` : "."}
          </div>
        )}

        <div className="space-y-3">
          {runs.map((run) => (
            <div
              key={run.partitionKey}
              className="bg-white dark:bg-gray-800 rounded-lg shadow p-4 flex flex-wrap items-center justify-between gap-3"
            >
              <div className="flex-1 min-w-0">
                <div className="text-sm font-mono text-gray-900 dark:text-white truncate">{run.tenantId}</div>
                <div className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                  Run: <span className="font-mono">{run.historyRowKey}</span> · Archived{" "}
                  {new Date(run.archivedAt).toLocaleString()}
                </div>
                <div className="text-xs text-gray-600 dark:text-gray-300 mt-1 space-x-3">
                  <span>Gather: <strong>{run.gatherRulesCount}</strong></span>
                  <span>Analyze: <strong>{run.analyzeRulesCount}</strong></span>
                  <span>ImePatterns: <strong>{run.imeLogPatternsCount}</strong></span>
                </div>
              </div>
              <div className="flex items-center gap-2">
                <Link
                  href={`/admin/customs-archive/${encodeURIComponent(run.tenantId)}/${encodeURIComponent(run.historyRowKey)}`}
                  className="px-3 py-1.5 bg-gray-200 hover:bg-gray-300 dark:bg-gray-700 dark:hover:bg-gray-600 text-gray-900 dark:text-white text-sm rounded-md transition-colors"
                >
                  Inspect
                </Link>
                <button
                  type="button"
                  onClick={() => setPendingDelete(run)}
                  disabled={deletingPk === run.partitionKey}
                  className="px-3 py-1.5 bg-red-600 hover:bg-red-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
                >
                  {deletingPk === run.partitionKey ? "Deleting…" : "Delete run"}
                </button>
              </div>
            </div>
          ))}
        </div>
      </main>

      <DeleteConfirmModal
        open={pendingDelete !== null}
        title="Delete archive run"
        description={pendingDelete && (
          <>
            <p>
              Delete the entire archive run for tenant{" "}
              <span className="font-mono">{pendingDelete.tenantId}</span>?
            </p>
            <p className="mt-2">
              This permanently removes{" "}
              <strong>
                {pendingDelete.gatherRulesCount +
                  pendingDelete.analyzeRulesCount +
                  pendingDelete.imeLogPatternsCount}
              </strong>{" "}
              archived rules. This action is irreversible.
            </p>
          </>
        )}
        busy={deletingPk !== null}
        onCancel={() => setPendingDelete(null)}
        onConfirm={confirmDelete}
      />
    </div>
  );
}
