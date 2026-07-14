"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../../AdminConfigContext";
import { AdminNotifications } from "../../AdminNotifications";
import { DeletionPreviewModal } from "./components/DeletionPreviewModal";
import { MaintenanceStatusBanner } from "./components/MaintenanceStatusBanner";
import { RestoreBrowserTab } from "./components/RestoreBrowserTab";
import { RestoreConfirmDialog } from "./components/RestoreConfirmDialog";

type CascadeState = "Preparing" | "Queued" | "Running" | "Poisoned";

interface SessionDeletionRow {
  tenantId: string;
  sessionId: string;
  deletionState: CascadeState | string;
  manifestId: string;
  timestamp: string;
  ageMinutes: number;
}

interface SessionDeletionListResponse {
  success: boolean;
  state: string;
  strandedSinceMinutes: number | null;
  count: number;
  sessions: SessionDeletionRow[];
}

type Tab = "in-flight" | "poisoned" | "stranded" | "restore-browser" | "activity";

const STRANDED_THRESHOLD_MINUTES = 30;

const IN_FLIGHT_STATES: CascadeState[] = ["Preparing", "Queued", "Running"];

export default function SessionCleanupPage() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();
  const [activeTab, setActiveTab] = useState<Tab>("in-flight");

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white">Session Cleanup</h1>
          <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
            V2 cascade-delete observability — In-Flight, Poisoned, Stranded queue, and lifecycle
            activity. Operator actions: preview the planned manifest, or restore from snapshot.
          </p>
        </div>
      </header>
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
        <AdminNotifications />

        <MaintenanceStatusBanner
          getAccessToken={getAccessToken}
          setError={setError}
          setSuccessMessage={setSuccessMessage}
        />

        {/* Tab bar */}
        <div className="border-b border-gray-200 dark:border-gray-700">
          <nav className="-mb-px flex gap-6 overflow-x-auto">
            <TabButton active={activeTab === "in-flight"} onClick={() => setActiveTab("in-flight")}>
              In-Flight
            </TabButton>
            <TabButton active={activeTab === "poisoned"} onClick={() => setActiveTab("poisoned")}>
              Poisoned
            </TabButton>
            <TabButton active={activeTab === "stranded"} onClick={() => setActiveTab("stranded")}>
              Stranded&nbsp;Queued
            </TabButton>
            <TabButton active={activeTab === "restore-browser"} onClick={() => setActiveTab("restore-browser")}>
              Restore&nbsp;Browser
            </TabButton>
            <TabButton active={activeTab === "activity"} onClick={() => setActiveTab("activity")}>
              Activity
            </TabButton>
          </nav>
        </div>

        {activeTab === "in-flight" && (
          <InFlightTab
            getAccessToken={getAccessToken}
            setError={setError}
            setSuccessMessage={setSuccessMessage}
          />
        )}
        {activeTab === "poisoned" && (
          <PoisonedTab
            getAccessToken={getAccessToken}
            setError={setError}
            setSuccessMessage={setSuccessMessage}
          />
        )}
        {activeTab === "stranded" && (
          <StrandedTab getAccessToken={getAccessToken} setError={setError} />
        )}
        {activeTab === "restore-browser" && (
          <RestoreBrowserTab
            getAccessToken={getAccessToken}
            setError={setError}
            setSuccessMessage={setSuccessMessage}
          />
        )}
        {activeTab === "activity" && <ActivityTab />}
      </main>
    </div>
  );
}

function TabButton({
  active,
  onClick,
  children,
}: {
  active: boolean;
  onClick: () => void;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      className={`py-3 px-1 border-b-2 text-sm font-medium whitespace-nowrap transition-colors ${
        active
          ? "border-purple-500 text-purple-600 dark:text-purple-300"
          : "border-transparent text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 hover:border-gray-300"
      }`}
    >
      {children}
    </button>
  );
}

// ── In-Flight tab ────────────────────────────────────────────────────────────

function InFlightTab({
  getAccessToken,
  setError,
  setSuccessMessage,
}: {
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}) {
  const [rows, setRows] = useState<SessionDeletionRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);
  const [previewing, setPreviewing] = useState<SessionDeletionRow | null>(null);

  const fetchAll = useCallback(async () => {
    setLoading(true);
    try {
      const results = await Promise.all(
        IN_FLIGHT_STATES.map((state) =>
          authenticatedFetch(api.sessionDeletions.list(state), getAccessToken).then(async (r) => {
            if (!r.ok) throw new Error(`HTTP ${r.status} for state=${state}`);
            return (await r.json()) as SessionDeletionListResponse;
          }),
        ),
      );
      const all = results.flatMap((r) => r.sessions);
      // Sort by age desc (oldest first → likely the one needing attention).
      all.sort((a, b) => b.ageMinutes - a.ageMinutes);
      setRows(all);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    void fetchAll();
  }, [fetchAll, refreshKey]);

  return (
    <>
      <SessionList
        title="Cascade in progress"
        description="Sessions where the cascade-delete pipeline is currently active. Empty list = no V2 cascade in flight across all tenants."
        rows={rows}
        loading={loading}
        onRefresh={() => setRefreshKey((k) => k + 1)}
        renderRowActions={(row) => (
          <button
            onClick={() => setPreviewing(row)}
            className="px-3 py-1 text-xs bg-blue-600 text-white rounded hover:bg-blue-700"
          >
            Preview
          </button>
        )}
        stateColors={{
          Preparing: "bg-amber-100 text-amber-800 dark:bg-amber-900/30 dark:text-amber-200",
          Queued: "bg-blue-100 text-blue-800 dark:bg-blue-900/30 dark:text-blue-200",
          Running: "bg-purple-100 text-purple-800 dark:bg-purple-900/30 dark:text-purple-200",
        }}
        emptyMessage="No active cascades."
        successMessage={null}
      />
      {previewing && (
        <DeletionPreviewModal
          tenantId={previewing.tenantId}
          sessionId={previewing.sessionId}
          manifestId={previewing.manifestId}
          onClose={() => setPreviewing(null)}
          getAccessToken={getAccessToken}
        />
      )}
    </>
  );
}

// ── Poisoned tab ─────────────────────────────────────────────────────────────

function PoisonedTab({
  getAccessToken,
  setError,
  setSuccessMessage,
}: {
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}) {
  const [rows, setRows] = useState<SessionDeletionRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);
  const [previewing, setPreviewing] = useState<SessionDeletionRow | null>(null);
  const [restoring, setRestoring] = useState<SessionDeletionRow | null>(null);

  const fetchPoisoned = useCallback(async () => {
    setLoading(true);
    try {
      const resp = await authenticatedFetch(api.sessionDeletions.list("Poisoned"), getAccessToken);
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const json = (await resp.json()) as SessionDeletionListResponse;
      const sorted = [...json.sessions].sort((a, b) => b.ageMinutes - a.ageMinutes);
      setRows(sorted);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    void fetchPoisoned();
  }, [fetchPoisoned, refreshKey]);

  return (
    <>
      <SessionList
        title="Poisoned cascades"
        description="Sessions where the cascade hit max-dequeue or live verification failed. They need an operator restore to recover."
        rows={rows}
        loading={loading}
        onRefresh={() => setRefreshKey((k) => k + 1)}
        renderRowActions={(row) => (
          <div className="flex gap-1">
            <button
              onClick={() => setPreviewing(row)}
              className="px-3 py-1 text-xs bg-gray-200 dark:bg-gray-700 text-gray-800 dark:text-gray-100 rounded hover:bg-gray-300 dark:hover:bg-gray-600"
            >
              Preview
            </button>
            <button
              onClick={() => setRestoring(row)}
              className="px-3 py-1 text-xs bg-amber-600 text-white rounded hover:bg-amber-700"
            >
              Restore…
            </button>
          </div>
        )}
        stateColors={{
          Poisoned: "bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-200",
        }}
        emptyMessage="No poisoned cascades — V2 is running clean."
        successMessage={null}
      />
      {previewing && (
        <DeletionPreviewModal
          tenantId={previewing.tenantId}
          sessionId={previewing.sessionId}
          manifestId={previewing.manifestId}
          onClose={() => setPreviewing(null)}
          getAccessToken={getAccessToken}
        />
      )}
      {restoring && (
        <RestoreConfirmDialog
          tenantId={restoring.tenantId}
          sessionId={restoring.sessionId}
          manifestId={restoring.manifestId}
          onClose={() => setRestoring(null)}
          onRestored={() => {
            // Codex P3: do NOT close the dialog here — the operator wants to see the real-run
            // result counts beside the dry-run preview before dismissing. The user closes the
            // dialog explicitly via Close / Start over. Background refresh of the Poisoned list
            // happens immediately so the row drops out when they look behind the modal.
            setSuccessMessage(`Session ${restoring.sessionId} restore submitted.`);
            setRefreshKey((k) => k + 1);
          }}
          getAccessToken={getAccessToken}
        />
      )}
    </>
  );
}

// ── Stranded tab ─────────────────────────────────────────────────────────────

function StrandedTab({
  getAccessToken,
  setError,
}: {
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  setError: (error: string | null) => void;
}) {
  const [rows, setRows] = useState<SessionDeletionRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshKey, setRefreshKey] = useState(0);
  const [previewing, setPreviewing] = useState<SessionDeletionRow | null>(null);

  const fetchStranded = useCallback(async () => {
    setLoading(true);
    try {
      const resp = await authenticatedFetch(
        api.sessionDeletions.list("Queued", STRANDED_THRESHOLD_MINUTES),
        getAccessToken,
      );
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const json = (await resp.json()) as SessionDeletionListResponse;
      const sorted = [...json.sessions].sort((a, b) => b.ageMinutes - a.ageMinutes);
      setRows(sorted);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired; reload the page and try again.");
      } else {
        setError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    void fetchStranded();
  }, [fetchStranded, refreshKey]);

  return (
    <>
      <SessionList
        title="Stranded queued"
        description={`Sessions in DeletionState=Queued for more than ${STRANDED_THRESHOLD_MINUTES} minutes — the worker hasn't picked them up. Matches the SessionDeletionStrandedQueued OpsEvent watchdog.`}
        rows={rows}
        loading={loading}
        onRefresh={() => setRefreshKey((k) => k + 1)}
        renderRowActions={(row) => (
          <button
            onClick={() => setPreviewing(row)}
            className="px-3 py-1 text-xs bg-blue-600 text-white rounded hover:bg-blue-700"
          >
            Preview
          </button>
        )}
        stateColors={{
          Queued: "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/30 dark:text-yellow-200",
        }}
        emptyMessage="No stranded queued cascades."
        successMessage={null}
      />
      {previewing && (
        <DeletionPreviewModal
          tenantId={previewing.tenantId}
          sessionId={previewing.sessionId}
          manifestId={previewing.manifestId}
          onClose={() => setPreviewing(null)}
          getAccessToken={getAccessToken}
        />
      )}
    </>
  );
}

// ── Activity tab ─────────────────────────────────────────────────────────────

function ActivityTab() {
  return (
    <div className="bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg p-6">
      <h2 className="text-lg font-medium text-gray-900 dark:text-white mb-2">Cascade-Delete Activity</h2>
      <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
        Tenant audit-log action strings for V2 cascade lifecycle:
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">deletion_started</code>,
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">deletion_completed</code>,
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">deletion_restored</code>.
        Retention-driven cascades land as
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">SessionDeletionMaintenanceFanout</code>
        per tenant (aggregate counts) plus the individual <code className="px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">deletion_started</code>/<code className="px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">deletion_completed</code> rows.
      </p>
      <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
        Step-level progress (per-table deletes, verification residuals) lives in DeletionProgress
        blobs and structured logs — not in the tenant audit trail. Operator-scoped signals are
        emitted as OpsEvents instead:
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">SessionDeletionPoisoned</code>
        (max-dequeue or verification failure — see Poisoned tab),
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">SessionDeletionStrandedQueued</code>
        (Stranded tab),
        <code className="mx-1 px-1 bg-gray-100 dark:bg-gray-700 rounded text-xs">SessionDeletionMaintenanceFanoutSkipped</code>
        (kill-switch active during a retention sweep). All available on the Ops Events page and
        wirable for Telegram alerts.
      </p>
      <Link
        href="/audit"
        className="inline-flex items-center px-4 py-2 text-sm bg-purple-600 text-white rounded hover:bg-purple-700"
      >
        Open audit log →
      </Link>
    </div>
  );
}

// ── Shared list component ────────────────────────────────────────────────────

function SessionList({
  title,
  description,
  rows,
  loading,
  onRefresh,
  renderRowActions,
  stateColors,
  emptyMessage,
}: {
  title: string;
  description: string;
  rows: SessionDeletionRow[];
  loading: boolean;
  onRefresh: () => void;
  renderRowActions: (row: SessionDeletionRow) => React.ReactNode;
  stateColors: Record<string, string>;
  emptyMessage: string;
  successMessage: string | null;
}) {
  return (
    <div className="bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg">
      <div className="px-6 py-4 border-b border-gray-200 dark:border-gray-700 flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-medium text-gray-900 dark:text-white">{title}</h2>
          <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">{description}</p>
        </div>
        <button
          onClick={onRefresh}
          disabled={loading}
          className="px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded text-gray-700 dark:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50 whitespace-nowrap"
        >
          {loading ? "Loading…" : "Refresh"}
        </button>
      </div>

      {loading && (
        <div className="px-6 py-8 text-center text-sm text-gray-500 dark:text-gray-400">Loading…</div>
      )}
      {!loading && rows.length === 0 && (
        <div className="px-6 py-8 text-center text-sm text-gray-500 dark:text-gray-400">{emptyMessage}</div>
      )}
      {!loading && rows.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 dark:bg-gray-900">
              <tr>
                <th className="text-left px-4 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Tenant</th>
                <th className="text-left px-4 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Session</th>
                <th className="text-left px-4 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">State</th>
                <th className="text-left px-4 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Manifest</th>
                <th className="text-right px-4 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Age</th>
                <th className="text-right px-4 py-2 text-xs font-medium text-gray-600 dark:text-gray-400 uppercase">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-200 dark:divide-gray-700">
              {rows.map((row) => (
                <tr key={`${row.tenantId}-${row.sessionId}`}>
                  <td className="px-4 py-2 font-mono text-xs text-gray-700 dark:text-gray-300">{row.tenantId}</td>
                  <td className="px-4 py-2 font-mono text-xs text-gray-700 dark:text-gray-300">{row.sessionId}</td>
                  <td className="px-4 py-2">
                    <span
                      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                        stateColors[row.deletionState] ??
                        "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-200"
                      }`}
                    >
                      {row.deletionState}
                    </span>
                  </td>
                  <td className="px-4 py-2 font-mono text-xs text-gray-500 dark:text-gray-400 truncate max-w-[12rem]" title={row.manifestId}>
                    {row.manifestId || "—"}
                  </td>
                  <td className="px-4 py-2 text-right text-xs text-gray-600 dark:text-gray-400">
                    {formatAge(row.ageMinutes)}
                  </td>
                  <td className="px-4 py-2 text-right">{renderRowActions(row)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function formatAge(minutes: number): string {
  if (minutes < 60) return `${minutes}m`;
  if (minutes < 60 * 24) return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
  const days = Math.floor(minutes / (60 * 24));
  const hours = Math.floor((minutes - days * 60 * 24) / 60);
  return `${days}d ${hours}h`;
}
