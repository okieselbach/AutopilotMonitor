"use client";

import { Suspense, useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  api,
  type BackupManifest,
  type BackupTableEntry,
  type RestoreRowCommitResponse,
  type RestoreRowPreviewResponse,
  type RestoreRowRequestBody,
} from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../../AdminConfigContext";
import { AdminNotifications } from "../../AdminNotifications";
import { RestoreRowDiffModal } from "../components/RestoreRowDiffModal";

/**
 * Backup manifest detail page (plan §PR2). Renders the per-table breakdown
 * (status / row-count / byte-size / SHA-256 preview / error message) and lets
 * the operator restore a single row from any Ok / Empty table.
 */
export default function BackupDetailPage() {
  // useSearchParams() in BackupDetailContent requires a Suspense boundary for static prerender.
  return (
    <Suspense fallback={null}>
      <BackupDetailContent />
    </Suspense>
  );
}

function BackupDetailContent() {
  const searchParams = useSearchParams();
  const backupId = searchParams?.get("id") ?? "";
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();

  const [manifest, setManifest] = useState<BackupManifest | null>(null);
  const [loading, setLoading] = useState<boolean>(false);

  // Restore-row trigger state.
  const [restoreInput, setRestoreInput] = useState<{ tableName: string; pk: string; rk: string } | null>(null);
  const [previewLoading, setPreviewLoading] = useState<boolean>(false);
  const [activePreview, setActivePreview] = useState<RestoreRowPreviewResponse | null>(null);

  const load = useCallback(async () => {
    if (!backupId) return;
    try {
      setLoading(true);
      const res = await authenticatedFetch(api.backups.manifest(backupId), getAccessToken);
      if (!res.ok) throw new Error(`Manifest failed: ${res.status} ${res.statusText}`);
      const body = (await res.json()) as BackupManifest;
      setManifest(body);
    } catch (err) {
      if (err instanceof TokenExpiredError) setError(err.message);
      else setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, [backupId, getAccessToken, setError]);

  useEffect(() => {
    void load();
  }, [load]);

  const openRestoreModal = async (tableName: string, pk: string, rk: string) => {
    setError(null);
    setPreviewLoading(true);
    try {
      const body: RestoreRowRequestBody = {
        tableName,
        partitionKey: pk,
        rowKey: rk,
        mode: "Preview",
      };
      const res = await authenticatedFetch(api.backups.restoreRow(backupId), getAccessToken, {
        method: "POST",
        body: JSON.stringify(body),
        headers: { "Content-Type": "application/json" },
      });
      if (!res.ok) {
        const text = await res.text();
        throw new Error(`Preview failed: ${res.status} ${text}`);
      }
      const preview = (await res.json()) as RestoreRowPreviewResponse;
      setActivePreview(preview);
    } catch (err) {
      if (err instanceof TokenExpiredError) setError(err.message);
      else setError(err instanceof Error ? err.message : String(err));
    } finally {
      setPreviewLoading(false);
    }
  };

  const handleCommit = useCallback(
    (response: RestoreRowCommitResponse) => {
      setSuccessMessage(
        `Row ${response.outcome.toLowerCase()}: ${response.tableName} pk='${response.partitionKey}' rk='${response.rowKey}'`,
      );
      setActivePreview(null);
      setRestoreInput(null);
    },
    [setSuccessMessage],
  );

  const sortedTables = useMemo(() => {
    if (!manifest) return [];
    return [...manifest.tables].sort((a, b) => a.tableName.localeCompare(b.tableName));
  }, [manifest]);

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <div className="text-sm">
            <Link href="/admin/backups" className="text-blue-600 dark:text-blue-400 hover:underline">
              ← Backups
            </Link>
          </div>
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white mt-2 font-mono">{backupId}</h1>
          {manifest && (
            <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
              Started {new Date(manifest.startedAtUtc).toLocaleString()} · completed{" "}
              {new Date(manifest.completedAtUtc).toLocaleString()} · triggered by{" "}
              <span className="font-mono">{manifest.triggeredBy}</span> · outcome{" "}
              <OutcomePill outcome={manifest.outcome} />
            </p>
          )}
        </div>
      </header>

      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
        <AdminNotifications />

        {!manifest && !loading && (
          <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6 text-sm text-gray-600 dark:text-gray-400">
            Manifest not found — backup may be incomplete or deleted.
          </div>
        )}

        {manifest && (
          <>
            {/* Restore-row trigger card */}
            <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-4 space-y-3">
              <h2 className="text-sm font-medium text-gray-900 dark:text-white">Restore a single row</h2>
              <p className="text-xs text-gray-600 dark:text-gray-400">
                Enter the table name + partition key + row key. The preview will show the diff between the backup
                row and the live row; you confirm the restore in the modal.
              </p>
              <RestoreInputRow
                tables={manifest.tables}
                disabled={previewLoading || activePreview !== null}
                onSubmit={(tableName, pk, rk) => {
                  setRestoreInput({ tableName, pk, rk });
                  void openRestoreModal(tableName, pk, rk);
                }}
              />
              {previewLoading && restoreInput && (
                <div className="text-xs text-gray-500 dark:text-gray-400">
                  Loading preview for <span className="font-mono">{restoreInput.tableName}</span> /{" "}
                  <span className="font-mono">{restoreInput.pk}</span> /{" "}
                  <span className="font-mono">{restoreInput.rk}</span>…
                </div>
              )}
            </div>

            {/* Per-table breakdown */}
            <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
              <table className="w-full text-xs">
                <thead className="bg-gray-100 dark:bg-gray-900 text-gray-700 dark:text-gray-300">
                  <tr>
                    <th className="px-3 py-2 text-left">Table</th>
                    <th className="px-3 py-2 text-left">Status</th>
                    <th className="px-3 py-2 text-right">Rows</th>
                    <th className="px-3 py-2 text-right">Bytes</th>
                    <th className="px-3 py-2 text-left">SHA-256 (preview)</th>
                    <th className="px-3 py-2 text-left">Notes</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
                  {sortedTables.map((t) => (
                    <tr key={t.tableName}>
                      <td className="px-3 py-2 font-mono text-gray-900 dark:text-white">{t.tableName}</td>
                      <td className="px-3 py-2"><StatusPill status={t.status} /></td>
                      <td className="px-3 py-2 text-right font-mono">{t.rowCount.toLocaleString()}</td>
                      <td className="px-3 py-2 text-right font-mono">{t.byteSize.toLocaleString()}</td>
                      <td className="px-3 py-2 font-mono text-gray-700 dark:text-gray-300">
                        {t.sha256Hex ? t.sha256Hex.slice(0, 12) + "…" : "—"}
                      </td>
                      <td className="px-3 py-2 text-red-700 dark:text-red-300">{t.errorMessage ?? ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </main>

      {activePreview && (
        <RestoreRowDiffModal
          backupId={backupId}
          preview={activePreview}
          getAccessToken={getAccessToken}
          onClose={() => {
            setActivePreview(null);
            setRestoreInput(null);
          }}
          onCommitted={handleCommit}
        />
      )}
    </div>
  );
}

function RestoreInputRow({
  tables,
  disabled,
  onSubmit,
}: {
  tables: BackupTableEntry[];
  disabled: boolean;
  onSubmit: (tableName: string, pk: string, rk: string) => void;
}) {
  const restorable = useMemo(
    () => tables.filter((t) => t.status === "Ok" || t.status === "Empty"),
    [tables],
  );
  const [tableName, setTableName] = useState<string>(restorable[0]?.tableName ?? "");
  const [pk, setPk] = useState<string>("");
  const [rk, setRk] = useState<string>("");

  // Keep the dropdown in sync if the manifest reload changes the restorable set.
  useEffect(() => {
    if (!restorable.find((t) => t.tableName === tableName)) {
      setTableName(restorable[0]?.tableName ?? "");
    }
  }, [restorable, tableName]);

  return (
    <div className="flex flex-wrap items-end gap-2">
      <label className="flex flex-col gap-1 text-xs text-gray-700 dark:text-gray-300">
        Table
        <select
          value={tableName}
          onChange={(e) => setTableName(e.target.value)}
          className="px-2 py-1 border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-800 text-sm font-mono"
        >
          {restorable.map((t) => (
            <option key={t.tableName} value={t.tableName}>{t.tableName}</option>
          ))}
        </select>
      </label>
      <label className="flex flex-col gap-1 text-xs text-gray-700 dark:text-gray-300 flex-1 min-w-40">
        Partition key
        <input
          type="text"
          value={pk}
          onChange={(e) => setPk(e.target.value)}
          className="px-2 py-1 border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-800 text-sm font-mono"
        />
      </label>
      <label className="flex flex-col gap-1 text-xs text-gray-700 dark:text-gray-300 flex-1 min-w-40">
        Row key
        <input
          type="text"
          value={rk}
          onChange={(e) => setRk(e.target.value)}
          className="px-2 py-1 border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-800 text-sm font-mono"
        />
      </label>
      <button
        type="button"
        onClick={() => onSubmit(tableName, pk, rk)}
        // Backend contract (RestoreTablePreflightValidator) allows empty PK/RK
        // — only null is rejected. Match that here so an Azure Table row with
        // a literal empty key (legal per spec) can be previewed/restored.
        disabled={disabled || !tableName}
        className="px-3 py-1.5 bg-green-600 hover:bg-green-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
      >
        Preview restore
      </button>
    </div>
  );
}

function OutcomePill({ outcome }: { outcome: "Success" | "Partial" }) {
  if (outcome === "Success") {
    return <span className="inline-block px-2 py-0.5 rounded text-xs bg-green-100 dark:bg-green-900 text-green-900 dark:text-green-100">Success</span>;
  }
  return <span className="inline-block px-2 py-0.5 rounded text-xs bg-yellow-100 dark:bg-yellow-900 text-yellow-900 dark:text-yellow-100">Partial</span>;
}

function StatusPill({ status }: { status: BackupTableEntry["status"] }) {
  switch (status) {
    case "Ok":
      return <span className="inline-block px-2 py-0.5 rounded text-xs bg-green-100 dark:bg-green-900 text-green-900 dark:text-green-100">Ok</span>;
    case "Empty":
      return <span className="inline-block px-2 py-0.5 rounded text-xs bg-gray-100 dark:bg-gray-700 text-gray-700 dark:text-gray-300">Empty</span>;
    case "Skipped":
      return <span className="inline-block px-2 py-0.5 rounded text-xs bg-yellow-100 dark:bg-yellow-900 text-yellow-900 dark:text-yellow-100">Skipped</span>;
    case "Failed":
      return <span className="inline-block px-2 py-0.5 rounded text-xs bg-red-100 dark:bg-red-900 text-red-900 dark:text-red-100">Failed</span>;
  }
}
