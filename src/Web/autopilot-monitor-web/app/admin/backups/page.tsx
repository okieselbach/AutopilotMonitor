"use client";

import { backupUrl } from "@/lib/routes";
import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import {
  api,
  type BackupJobStatus,
  type BackupListResponse,
  type BackupTriggerResponse,
} from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../AdminConfigContext";
import { AdminNotifications } from "../AdminNotifications";

const POLL_INTERVAL_MS = 2500;

/**
 * GA-only admin surface for the critical-table backup feature (plan §PR2).
 * Lists every completed backup run newest-first and offers a "Run backup now"
 * trigger that opens a status-poll banner while the BackgroundService worker
 * processes the queued job.
 */
export default function BackupsListPage() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();
  const [backupIds, setBackupIds] = useState<string[]>([]);
  const [loading, setLoading] = useState<boolean>(false);
  const [triggering, setTriggering] = useState<boolean>(false);
  const [activeJob, setActiveJob] = useState<BackupJobStatus | null>(null);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      const res = await authenticatedFetch(api.backups.list(), getAccessToken);
      if (!res.ok) throw new Error(`List backups failed: ${res.status} ${res.statusText}`);
      const body = (await res.json()) as BackupListResponse;
      setBackupIds(body.backupIds ?? []);
    } catch (err) {
      if (err instanceof TokenExpiredError) setError(err.message);
      else setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    void load();
  }, [load]);

  // Poll the active job until terminal, then refresh the list.
  useEffect(() => {
    if (!activeJob) return;
    if (
      activeJob.state === "Completed" ||
      activeJob.state === "Failed" ||
      activeJob.state === "Skipped" ||
      activeJob.state === "BlockedTerminal"
    ) {
      // Terminal — final refresh and stop polling.
      void load();
      return;
    }

    let cancelled = false;
    const timer = window.setTimeout(async () => {
      if (cancelled) return;
      try {
        const res = await authenticatedFetch(api.backups.jobStatus(activeJob.jobId), getAccessToken);
        if (!res.ok) throw new Error(`Job status failed: ${res.status} ${res.statusText}`);
        const body = (await res.json()) as BackupJobStatus;
        setActiveJob(body);
      } catch (err) {
        if (err instanceof TokenExpiredError) setError(err.message);
        else setError(err instanceof Error ? err.message : String(err));
        setActiveJob(null);
      }
    }, POLL_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [activeJob, getAccessToken, load, setError]);

  const triggerBackup = useCallback(async () => {
    try {
      setTriggering(true);
      const res = await authenticatedFetch(api.backups.trigger(), getAccessToken, {
        method: "POST",
      });
      if (res.status !== 202 && !res.ok) {
        const text = await res.text();
        throw new Error(`Trigger backup failed: ${res.status} ${text}`);
      }
      const body = (await res.json()) as BackupTriggerResponse;
      // Seed the poller with the freshly-created job.
      setActiveJob({
        jobId: body.jobId,
        kind: "Backup",
        state: "Queued",
        requestedBy: "",
        queuedAtUtc: new Date().toISOString(),
        lastHeartbeatUtc: new Date().toISOString(),
      });
      setSuccessMessage(`Backup queued — jobId ${body.jobId}.`);
    } catch (err) {
      if (err instanceof TokenExpiredError) setError(err.message);
      else setError(err instanceof Error ? err.message : String(err));
    } finally {
      setTriggering(false);
    }
  }, [getAccessToken, setError, setSuccessMessage]);

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white">Critical-Table Backups</h1>
          <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
            Daily snapshots of the 15 critical configuration tables (AdminConfiguration, AnalyzeRules, GatherRules,
            ImeLogPatterns, …). Manifests live in the <code className="font-mono">critical-table-backups</code> blob
            container; 90-day lifecycle delete. Inspect any backup to see per-table SHA + row counts, and restore a
            single row from there.
          </p>
        </div>
      </header>

      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
        <AdminNotifications />

        {/* Trigger row */}
        <div className="flex flex-wrap items-center gap-2">
          <button
            type="button"
            onClick={triggerBackup}
            disabled={triggering || activeJob !== null}
            className="px-3 py-1.5 bg-blue-600 hover:bg-blue-700 disabled:opacity-50 text-white text-sm rounded-md transition-colors"
          >
            {triggering ? "Triggering…" : "Run backup now"}
          </button>
          <button
            type="button"
            onClick={load}
            disabled={loading}
            className="px-3 py-1.5 bg-gray-200 hover:bg-gray-300 dark:bg-gray-700 dark:hover:bg-gray-600 text-gray-900 dark:text-white text-sm rounded-md transition-colors"
          >
            {loading ? "Loading…" : "Refresh"}
          </button>
          <span className="text-sm text-gray-500 dark:text-gray-400 ml-2">
            {backupIds.length} backup(s)
          </span>
        </div>

        {/* Active job banner */}
        {activeJob && <ActiveJobBanner job={activeJob} onDismiss={() => setActiveJob(null)} />}

        {/* List */}
        {backupIds.length === 0 && !loading && (
          <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6 text-sm text-gray-600 dark:text-gray-400">
            No critical-table backups found. The daily timer runs at 04:00 UTC; you can also trigger one manually
            via the button above.
          </div>
        )}

        <div className="space-y-2">
          {backupIds.map((id) => (
            <Link
              key={id}
              href={backupUrl(id)}
              className="block bg-white dark:bg-gray-800 rounded-lg shadow p-4 hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
            >
              <div className="font-mono text-sm text-gray-900 dark:text-white">{id}</div>
              <div className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                {formatBackupTimestamp(id)} · click to inspect manifest
              </div>
            </Link>
          ))}
        </div>
      </main>
    </div>
  );
}

function ActiveJobBanner({ job, onDismiss }: { job: BackupJobStatus; onDismiss: () => void }) {
  const state = job.state;
  const color =
    state === "Completed"
      ? "bg-green-100 dark:bg-green-900 border-green-300 dark:border-green-700 text-green-900 dark:text-green-100"
      : state === "Failed" || state === "BlockedTerminal"
      ? "bg-red-100 dark:bg-red-900 border-red-300 dark:border-red-700 text-red-900 dark:text-red-100"
      : state === "Skipped"
      ? "bg-yellow-100 dark:bg-yellow-900 border-yellow-300 dark:border-yellow-700 text-yellow-900 dark:text-yellow-100"
      : "bg-blue-100 dark:bg-blue-900 border-blue-300 dark:border-blue-700 text-blue-900 dark:text-blue-100";

  return (
    <div className={`border rounded-md p-3 text-sm ${color}`}>
      <div className="flex items-start justify-between gap-2">
        <div>
          <div className="font-medium">Backup job {job.jobId} — {state}</div>
          {job.backupOutcome && <div className="text-xs mt-0.5">Outcome: {job.backupOutcome}</div>}
          {job.error && <div className="text-xs mt-1 font-mono break-all">Error: {job.error}</div>}
          {job.progress && <div className="text-xs mt-1 font-mono break-all">Progress: {job.progress}</div>}
        </div>
        <button
          type="button"
          onClick={onDismiss}
          className="text-xs px-2 py-1 underline"
        >
          Dismiss
        </button>
      </div>
    </div>
  );
}

/** Pretty-print the embedded timestamp portion of a backupId (yyyyMMddTHHmmssZ_xxxxxxxx). */
function formatBackupTimestamp(backupId: string): string {
  // 20260522T040000Z_a1b2c3d4 → "2026-05-22 04:00:00 UTC"
  const m = backupId.match(/^(\d{4})(\d{2})(\d{2})T(\d{2})(\d{2})(\d{2})Z_/);
  if (!m) return backupId;
  return `${m[1]}-${m[2]}-${m[3]} ${m[4]}:${m[5]}:${m[6]} UTC`;
}
