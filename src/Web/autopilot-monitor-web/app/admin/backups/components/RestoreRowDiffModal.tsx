"use client";

import { useState } from "react";
import {
  api,
  type RestoreRowCommitResponse,
  type RestoreRowPreviewResponse,
  type RestoreRowPropertyDiff,
  type RestoreRowPropertySnapshot,
  type RestoreRowRequestBody,
} from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface RestoreRowDiffModalProps {
  backupId: string;
  preview: RestoreRowPreviewResponse;
  getAccessToken: () => Promise<string | null>;
  onClose: () => void;
  onCommitted: (response: RestoreRowCommitResponse) => void;
}

/**
 * Two-step modal: preview is loaded by the caller (so the diff is already
 * populated when the modal opens), then the operator can either dismiss or
 * commit. Commit echoes <code>ifSha256</code> + <code>ifCurrentETag</code>
 * from the preview response; the backend verifies both under the maintenance
 * lease before writing.
 */
export function RestoreRowDiffModal({
  backupId,
  preview,
  getAccessToken,
  onClose,
  onCommitted,
}: RestoreRowDiffModalProps) {
  const [committing, setCommitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const commit = async () => {
    setError(null);
    setCommitting(true);
    try {
      const body: RestoreRowRequestBody = {
        tableName: preview.tableName,
        partitionKey: preview.partitionKey,
        rowKey: preview.rowKey,
        mode: "Commit",
        ifSha256: preview.rowSha256,
        ifCurrentETag: preview.currentETag ?? null,
      };
      const res = await authenticatedFetch(api.backups.restoreRow(backupId), getAccessToken, {
        method: "POST",
        body: JSON.stringify(body),
        headers: { "Content-Type": "application/json" },
      });
      if (!res.ok) {
        const text = await res.text();
        throw new Error(`Restore failed: ${res.status} ${text}`);
      }
      const response = (await res.json()) as RestoreRowCommitResponse;
      onCommitted(response);
    } catch (err) {
      if (err instanceof TokenExpiredError) setError(err.message);
      else setError(err instanceof Error ? err.message : String(err));
    } finally {
      setCommitting(false);
    }
  };

  const diffCounts = countDiff(preview.diff);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
      <div className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-4xl w-full max-h-[90vh] overflow-hidden flex flex-col">
        <header className="px-6 py-4 border-b border-gray-200 dark:border-gray-700">
          <h2 className="text-lg font-medium text-gray-900 dark:text-white">
            Restore row from backup
          </h2>
          <div className="text-xs text-gray-500 dark:text-gray-400 mt-0.5 font-mono">
            {preview.tableName} · pk=<span className="text-gray-900 dark:text-gray-100">{preview.partitionKey}</span>{" "}
            · rk=<span className="text-gray-900 dark:text-gray-100">{preview.rowKey}</span>
          </div>
        </header>

        <div className="flex-1 overflow-y-auto px-6 py-4 space-y-4">
          {preview.isAuthTable && (
            <div className="border border-amber-400 bg-amber-50 dark:bg-amber-950 text-amber-900 dark:text-amber-100 rounded-md p-3 text-sm">
              <div className="font-medium">Security-relevant table — check the <code className="font-mono">IsEnabled</code> column</div>
              <div className="mt-1">
                This is an <strong>authentication / authorization</strong> table
                (<code className="font-mono">GlobalAdmins</code>, <code className="font-mono">TenantAdmins</code>,{" "}
                <code className="font-mono">McpUsers</code>). Restoring this row will overwrite the live{" "}
                <code className="font-mono">IsEnabled</code> flag — confirm that the backup row&apos;s enable/disable
                state is what you intend.
              </div>
            </div>
          )}

          <div className="text-sm text-gray-700 dark:text-gray-300">
            {preview.currentETag === null ? (
              <span>
                Live row <strong>does not exist</strong> — restore will <strong>insert</strong> the backup row.
              </span>
            ) : (
              <span>
                Live row exists (ETag <code className="font-mono">{preview.currentETag}</code>) — restore will{" "}
                <strong>replace</strong> it with the backup row.
              </span>
            )}
          </div>

          <div className="text-xs text-gray-600 dark:text-gray-400">
            <span className="font-medium">{diffCounts.changed}</span> changed ·{" "}
            <span className="font-medium">{diffCounts.added}</span> added ·{" "}
            <span className="font-medium text-red-700 dark:text-red-300">{diffCounts.removed}</span> will be removed ·{" "}
            <span className="font-medium">{diffCounts.unchanged}</span> unchanged
          </div>

          <div className="border border-gray-200 dark:border-gray-700 rounded-md overflow-hidden">
            <table className="w-full text-xs font-mono">
              <thead className="bg-gray-100 dark:bg-gray-900 text-gray-700 dark:text-gray-300">
                <tr>
                  <th className="px-3 py-2 text-left">Property</th>
                  <th className="px-3 py-2 text-left">Backup</th>
                  <th className="px-3 py-2 text-left">Current</th>
                  <th className="px-3 py-2 text-left">Change</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
                {preview.diff.map((d) => (
                  <tr key={d.name} className={rowClass(d)}>
                    <td className="px-3 py-2 align-top break-all">{d.name}</td>
                    <td className="px-3 py-2 align-top break-all whitespace-pre-wrap">{snapshotPretty(d.backup)}</td>
                    <td className="px-3 py-2 align-top break-all whitespace-pre-wrap">{snapshotPretty(d.current)}</td>
                    <td className="px-3 py-2 align-top">{kindLabel(d.kind)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {error && (
            <div className="border border-red-400 bg-red-50 dark:bg-red-950 text-red-900 dark:text-red-100 rounded-md p-3 text-sm font-mono break-all">
              {error}
            </div>
          )}
        </div>

        <footer className="px-6 py-3 border-t border-gray-200 dark:border-gray-700 flex justify-end gap-2">
          <button
            type="button"
            onClick={onClose}
            disabled={committing}
            className="px-3 py-1.5 bg-gray-200 hover:bg-gray-300 dark:bg-gray-700 dark:hover:bg-gray-600 text-gray-900 dark:text-white text-sm rounded-md transition-colors"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={commit}
            disabled={committing}
            className="px-3 py-1.5 bg-green-600 hover:bg-green-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
          >
            {committing ? "Restoring…" : "Restore row"}
          </button>
        </footer>
      </div>
    </div>
  );
}

function countDiff(diff: RestoreRowPropertyDiff[]): { added: number; removed: number; changed: number; unchanged: number } {
  const c = { added: 0, removed: 0, changed: 0, unchanged: 0 };
  for (const d of diff) {
    if (d.kind === "Added") c.added++;
    else if (d.kind === "Removed") c.removed++;
    else if (d.kind === "Changed") c.changed++;
    else c.unchanged++;
  }
  return c;
}

function rowClass(d: RestoreRowPropertyDiff): string {
  switch (d.kind) {
    case "Added":
      return "bg-green-50 dark:bg-green-950/30";
    case "Removed":
      return "bg-red-50 dark:bg-red-950/30";
    case "Changed":
      return "bg-yellow-50 dark:bg-yellow-950/30";
    default:
      return "";
  }
}

function kindLabel(kind: RestoreRowPropertyDiff["kind"]): string {
  switch (kind) {
    case "Added":
      return "will be added";
    case "Removed":
      return "will be removed";
    case "Changed":
      return "will be overwritten";
    case "Unchanged":
      return "unchanged";
  }
}

function snapshotPretty(snap: RestoreRowPropertySnapshot | null | undefined): string {
  if (snap == null) return "—";
  return `${JSON.stringify(snap.value)}\n[${snap.edmType}]`;
}
