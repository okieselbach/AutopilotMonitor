"use client";

import { useCallback, useEffect, useState, use as usePromise } from "react";
import Link from "next/link";
import {
  api,
  type CustomsArchiveEntrySummary,
  type CustomsArchiveFullEntry,
  type CustomsArchiveListEntriesResponse,
} from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../../../AdminConfigContext";
import { AdminNotifications } from "../../../AdminNotifications";
import { DeleteConfirmModal } from "../../components/DeleteConfirmModal";

interface PageProps {
  params: Promise<{ tenantId: string; historyRowKey: string }>;
}

export default function CustomsArchiveDetailPage({ params }: PageProps) {
  // Next.js 15: route params are async. Resolve once.
  const resolved = usePromise(params);
  const tenantId = decodeURIComponent(resolved.tenantId);
  const historyRowKey = decodeURIComponent(resolved.historyRowKey);

  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();
  const [entries, setEntries] = useState<CustomsArchiveEntrySummary[]>([]);
  const [loading, setLoading] = useState<boolean>(false);
  const [expanded, setExpanded] = useState<Record<string, CustomsArchiveFullEntry | "loading" | undefined>>({});
  const [deletingRk, setDeletingRk] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<CustomsArchiveEntrySummary | null>(null);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const url = api.customsArchive.listEntries(tenantId, historyRowKey);
      const res = await authenticatedFetch(url, getAccessToken);
      if (!res.ok) {
        throw new Error(`List entries failed: ${res.status} ${res.statusText}`);
      }
      const body = (await res.json()) as CustomsArchiveListEntriesResponse;
      setEntries(body.entries ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError(err.message);
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setLoading(false);
    }
  }, [tenantId, historyRowKey, getAccessToken, setError]);

  useEffect(() => {
    void load();
  }, [load]);

  const toggleExpand = useCallback(async (entry: CustomsArchiveEntrySummary) => {
    const key = entry.rowKey;
    if (expanded[key]) {
      // Collapse
      setExpanded((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
      return;
    }

    setExpanded((prev) => ({ ...prev, [key]: "loading" }));
    try {
      const res = await authenticatedFetch(
        api.customsArchive.getEntry(tenantId, historyRowKey, entry.rowKey),
        getAccessToken,
      );
      if (!res.ok) throw new Error(`Get entry failed: ${res.status}`);
      const body = await res.json();
      setExpanded((prev) => ({ ...prev, [key]: body.entry as CustomsArchiveFullEntry }));
    } catch (err) {
      setExpanded((prev) => {
        const next = { ...prev };
        delete next[key];
        return next;
      });
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [expanded, tenantId, historyRowKey, getAccessToken, setError]);

  const confirmDelete = useCallback(async () => {
    const entry = pendingDelete;
    if (!entry) return;
    try {
      setDeletingRk(entry.rowKey);
      const res = await authenticatedFetch(
        api.customsArchive.deleteEntry(tenantId, historyRowKey, entry.rowKey),
        getAccessToken,
        { method: "DELETE" },
      );
      if (!res.ok) throw new Error(`Delete failed: ${res.status}`);
      setSuccessMessage(`Deleted ${entry.originalTable} / ${entry.originalRowKey}`);
      setPendingDelete(null);
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setDeletingRk(null);
    }
  }, [pendingDelete, tenantId, historyRowKey, getAccessToken, load, setError, setSuccessMessage]);

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <Link
            href="/admin/customs-archive"
            className="text-sm text-blue-600 dark:text-blue-400 hover:underline"
          >
            ← Back to runs
          </Link>
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white mt-2">
            Customs Archive — Run detail
          </h1>
          <p className="text-xs font-mono text-gray-600 dark:text-gray-400 mt-1">
            Tenant: {tenantId} · Run: {historyRowKey}
          </p>
        </div>
      </header>

      <main className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-4">
        <AdminNotifications />

        <div className="flex items-center justify-between">
          <span className="text-sm text-gray-600 dark:text-gray-300">
            {entries.length} entry/entries{loading ? " — loading…" : ""}
          </span>
          <button
            type="button"
            onClick={load}
            disabled={loading}
            className="px-3 py-1.5 bg-blue-600 hover:bg-blue-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
          >
            {loading ? "Loading…" : "Refresh"}
          </button>
        </div>

        {entries.length === 0 && !loading && (
          <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6 text-sm text-gray-600 dark:text-gray-400">
            No archived entries for this run.
          </div>
        )}

        <div className="space-y-2">
          {entries.map((entry) => {
            const detail = expanded[entry.rowKey];
            const isOpen = !!detail;
            const isLoadingFull = detail === "loading";
            const fullEntry = typeof detail === "object" ? (detail as CustomsArchiveFullEntry) : null;

            return (
              <div
                key={entry.rowKey}
                className="bg-white dark:bg-gray-800 rounded-lg shadow border border-gray-200 dark:border-gray-700"
              >
                <div className="p-3 flex flex-wrap items-center justify-between gap-2">
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="inline-block px-2 py-0.5 rounded text-xs font-medium bg-indigo-100 text-indigo-800 dark:bg-indigo-900/40 dark:text-indigo-200">
                        {entry.originalTable}
                      </span>
                      <span className="text-sm font-mono text-gray-900 dark:text-white truncate">
                        {entry.originalRowKey}
                      </span>
                    </div>
                    <div className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                      Archived {new Date(entry.archivedAt).toLocaleString()}
                    </div>
                  </div>
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={() => toggleExpand(entry)}
                      className="px-3 py-1.5 bg-gray-200 hover:bg-gray-300 dark:bg-gray-700 dark:hover:bg-gray-600 text-gray-900 dark:text-white text-sm rounded-md transition-colors"
                    >
                      {isOpen ? "Collapse" : "Inspect"}
                    </button>
                    <button
                      type="button"
                      onClick={() => setPendingDelete(entry)}
                      disabled={deletingRk === entry.rowKey}
                      className="px-3 py-1.5 bg-red-600 hover:bg-red-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
                    >
                      {deletingRk === entry.rowKey ? "Deleting…" : "Delete entry"}
                    </button>
                  </div>
                </div>

                {isOpen && (
                  <div className="border-t border-gray-200 dark:border-gray-700 p-3 bg-gray-50 dark:bg-gray-900/50">
                    {isLoadingFull ? (
                      <div className="text-sm text-gray-500">Loading…</div>
                    ) : fullEntry ? (
                      <pre className="text-xs font-mono whitespace-pre-wrap break-all text-gray-800 dark:text-gray-200">
                        {prettyJson(fullEntry.entityJson)}
                      </pre>
                    ) : null}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </main>

      <DeleteConfirmModal
        open={pendingDelete !== null}
        title="Delete archive entry"
        description={pendingDelete && (
          <>
            <p>
              Delete this archived entry from{" "}
              <span className="font-mono">{pendingDelete.originalTable}</span>?
            </p>
            <p className="mt-2 font-mono text-xs break-all">
              {pendingDelete.originalRowKey}
            </p>
            <p className="mt-2">This action is irreversible.</p>
          </>
        )}
        busy={deletingRk !== null}
        onCancel={() => setPendingDelete(null)}
        onConfirm={confirmDelete}
      />
    </div>
  );
}

function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}
