"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface DeletionPreviewModalProps {
  tenantId: string;
  sessionId: string;
  manifestId: string;
  onClose: () => void;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}

interface ProgressView {
  snapshotSha256?: string;
  completedStepOrders?: number[];
  verificationDone?: boolean;
  tombstoneStarted?: boolean;
  completedAt?: string | null;
  aggregateDecrementsApplied?: number;
  restoreReIncrementsApplied?: number;
  lastFailureType?: string | null;
  lastFailureMessage?: string | null;
  /**
   * Verifier's OBSERVED residual count — capped at 50 per table and short-circuited at the
   * first failing table. When this equals 50 the real residual mountain is likely larger.
   */
  lastObservedResidualCount?: number | null;
  /** JSON array of {table, pk, rk}, capped at the residual sample size (50). */
  lastResidualSampleJson?: string | null;
}

/**
 * Matches the verifier cap in `CascadeVerificationService.MaxResidualSampleSize`. When the
 * observed count hits this value the operator should treat the number as a lower bound.
 */
const VERIFIER_OBSERVATION_CAP = 50;

interface StoredManifestSummary {
  manifestId?: string;
  schemaHash?: string;
  snapshotSha256?: string;
  estimatedRowCount?: number;
  estimatedSnapshotBytes?: number;
  preflightCounts?: Record<string, number>;
  sampleKeys?: Record<string, Array<{ pk: string; rk: string }>>;
  progress?: ProgressView | null;
  source?: string;
}

export function DeletionPreviewModal({
  tenantId,
  sessionId,
  manifestId,
  onClose,
  getAccessToken,
}: DeletionPreviewModalProps) {
  const [summary, setSummary] = useState<StoredManifestSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [downloading, setDownloading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const resp = await authenticatedFetch(
          api.sessionDeletions.storedManifest(sessionId, tenantId, manifestId, "summary"),
          getAccessToken,
        );
        if (!resp.ok) {
          const detail = await resp.text().catch(() => "");
          throw new Error(`Stored-manifest request failed: HTTP ${resp.status}${detail ? ` — ${detail.slice(0, 200)}` : ""}`);
        }
        const json = (await resp.json()) as StoredManifestSummary;
        if (!cancelled) setSummary(json);
      } catch (err) {
        if (cancelled) return;
        if (err instanceof TokenExpiredError) {
          setError("Session expired; reload the page and try again.");
        } else {
          setError(err instanceof Error ? err.message : String(err));
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [tenantId, sessionId, manifestId, getAccessToken]);

  const handleDownload = async () => {
    setDownloading(true);
    setError(null);
    try {
      const resp = await authenticatedFetch(
        api.sessionDeletions.storedManifest(sessionId, tenantId, manifestId, "download"),
        getAccessToken,
      );
      if (!resp.ok) {
        throw new Error(`Download failed: HTTP ${resp.status}`);
      }
      const blob = await resp.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `${tenantId}_${sessionId}_${manifestId}.snapshot.json.gz`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      // Browsers keep the Blob alive until the next gc once the object URL is revoked;
      // a small timeout keeps Firefox happy on slower disks.
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setDownloading(false);
    }
  };

  const progress = summary?.progress ?? null;
  const completedCount = progress?.completedStepOrders?.length ?? 0;
  const failureSample = (() => {
    if (!progress?.lastResidualSampleJson) return null;
    try {
      const arr = JSON.parse(progress.lastResidualSampleJson) as Array<{ table: string; pk: string; rk: string }>;
      return Array.isArray(arr) ? arr : null;
    } catch {
      return null;
    }
  })();

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50 p-4"
      onClick={onClose}
    >
      <div
        className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-3xl w-full max-h-[80vh] overflow-y-auto"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="sticky top-0 bg-white dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700 px-6 py-4 flex items-start justify-between rounded-t-lg">
          <div>
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Stored Manifest</h2>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 font-mono">
              {tenantId} / {sessionId}
            </p>
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
              Reads the persisted snapshot the worker captured at cascade start —
              <strong className="ml-1">not</strong> a fresh dry-run.
            </p>
          </div>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
            aria-label="Close"
          >
            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>

        <div className="px-6 py-4 space-y-4">
          {loading && <p className="text-sm text-gray-600 dark:text-gray-400">Loading stored manifest…</p>}
          {error && (
            <div className="p-3 bg-red-50 dark:bg-red-900/30 border border-red-200 dark:border-red-700 rounded text-sm text-red-700 dark:text-red-200">
              {error}
            </div>
          )}
          {!loading && !error && summary && (
            <>
              <div className="grid grid-cols-2 gap-3 text-sm">
                <Field label="Manifest" value={summary.manifestId ?? manifestId} mono />
                <Field label="Schema hash" value={summary.schemaHash ?? "—"} mono />
                <Field
                  label="Snapshot SHA-256"
                  value={summary.snapshotSha256 ?? "—"}
                  mono
                />
                <Field
                  label="Snapshot size"
                  value={
                    summary.estimatedSnapshotBytes != null
                      ? formatBytes(summary.estimatedSnapshotBytes)
                      : "—"
                  }
                />
              </div>

              {progress && (
                <div className="border border-purple-200 dark:border-purple-700 bg-purple-50 dark:bg-purple-900/20 rounded p-3 text-sm">
                  <h3 className="text-sm font-semibold text-purple-900 dark:text-purple-100 mb-2">Worker progress</h3>
                  <div className="grid grid-cols-2 gap-2 text-xs">
                    <span className="text-gray-700 dark:text-gray-300">Completed steps: <strong>{completedCount}</strong></span>
                    <span className="text-gray-700 dark:text-gray-300">Verification done: <strong>{progress.verificationDone ? "yes" : "no"}</strong></span>
                    <span className="text-gray-700 dark:text-gray-300">Tombstone started: <strong>{progress.tombstoneStarted ? "yes" : "no"}</strong></span>
                    <span className="text-gray-700 dark:text-gray-300">Aggregate decrements: <strong>{progress.aggregateDecrementsApplied ?? 0}</strong></span>
                  </div>
                  {progress.lastFailureType && (
                    <div className="mt-3 p-2 bg-red-100 dark:bg-red-900/40 rounded text-xs text-red-800 dark:text-red-100">
                      <div><strong>Last failure:</strong> <code>{progress.lastFailureType}</code></div>
                      {progress.lastFailureMessage && (
                        <div className="mt-1 break-words">{progress.lastFailureMessage}</div>
                      )}
                      {failureSample && failureSample.length > 0 && (() => {
                        const sampleSize = failureSample.length;
                        const observed = progress.lastObservedResidualCount ?? sampleSize;
                        const capHit = observed >= VERIFIER_OBSERVATION_CAP;
                        return (
                          <details className="mt-2">
                            <summary className="cursor-pointer text-red-700 dark:text-red-200">
                              Residual sample ({sampleSize} of {observed}{capHit ? "+" : ""} observed row{observed === 1 ? "" : "s"})
                            </summary>
                            <ul className="mt-1 pl-4 list-disc font-mono break-all">
                              {failureSample.slice(0, 20).map((r, i) => (
                                <li key={i}>{r.table}: {r.pk} / {r.rk}</li>
                              ))}
                            </ul>
                            {capHit && (
                              <p className="mt-2 text-xs italic text-red-700 dark:text-red-200">
                                The verifier short-circuits at {VERIFIER_OBSERVATION_CAP} rows per
                                table and after the first failing table — the real residual count
                                may be higher. Use the cascade-restore path to recover; do not rely
                                on this number as an exact blast-radius estimate.
                              </p>
                            )}
                          </details>
                        );
                      })()}
                    </div>
                  )}
                </div>
              )}

              {summary.preflightCounts && (
                <div>
                  <h3 className="text-sm font-semibold text-gray-700 dark:text-gray-200 mb-2">Rows in snapshot (by table)</h3>
                  <div className="border border-gray-200 dark:border-gray-700 rounded overflow-hidden">
                    <table className="w-full text-sm">
                      <thead className="bg-gray-50 dark:bg-gray-900">
                        <tr>
                          <th className="text-left px-3 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Table</th>
                          <th className="text-right px-3 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Count</th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
                        {Object.entries(summary.preflightCounts).map(([table, count]) => (
                          <tr key={table}>
                            <td className="px-3 py-2 font-mono text-xs text-gray-800 dark:text-gray-200">{table}</td>
                            <td className="px-3 py-2 text-right text-gray-800 dark:text-gray-200">{count}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}
            </>
          )}
        </div>

        <div className="sticky bottom-0 bg-gray-50 dark:bg-gray-900 px-6 py-3 border-t border-gray-200 dark:border-gray-700 flex justify-end gap-2 rounded-b-lg">
          <button
            onClick={handleDownload}
            disabled={loading || downloading || !!error}
            className="px-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded text-gray-700 dark:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50"
          >
            {downloading ? "Downloading…" : "Download snapshot"}
          </button>
          <button
            onClick={onClose}
            className="px-3 py-2 text-sm bg-gray-200 dark:bg-gray-700 text-gray-800 dark:text-gray-100 rounded hover:bg-gray-300 dark:hover:bg-gray-600"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}

function Field({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <p className="text-xs text-gray-500 dark:text-gray-400 uppercase">{label}</p>
      <p className={`text-sm text-gray-900 dark:text-white ${mono ? "font-mono break-all" : ""}`}>{value}</p>
    </div>
  );
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
}
