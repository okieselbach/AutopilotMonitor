"use client";

import { useCallback, useEffect, useMemo, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useAdminConfig } from "../../../AdminConfigContext";
import { DeletionManifestSummaryView } from "./DeletionManifestSummaryView";
import { RestoreConfirmDialog } from "./RestoreConfirmDialog";

interface RestoreBrowserTabProps {
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

interface ManifestEntry {
  manifestId: string;
  sizeBytes: number;
  lastModifiedUtc: string;
}

interface SessionGroup {
  sessionId: string;
  manifestCount: number;
  latestManifestUtc: string;
  manifests: ManifestEntry[];
}

interface TenantManifestsResponse {
  success: boolean;
  tenantId: string;
  sessionCount: number;
  manifestCount: number;
  sessions: SessionGroup[];
}

interface Selection {
  tenantId: string;
  sessionId: string;
  manifestId: string;
}

interface TenantsWithManifestsResponse {
  success: boolean;
  count: number;
  tenantIds: string[];
}

/**
 * File-browser-style picker for stored cascade-delete snapshots. Lets a Global Admin recover a
 * cleanly-completed cascade where the Sessions row is already gone — these sessions wouldn't
 * appear on the In-Flight / Poisoned / Stranded tabs, so without this entry point the operator
 * would need the manifestId in hand from the audit log.
 *
 * Layout: tenant dropdown up top → two columns: left = collapsible session/manifest tree;
 * right = the same DeletionManifestSummaryView the modal renders, plus a "Restore…" button
 * that opens the dialog with the selection pre-filled.
 */
export function RestoreBrowserTab({ getAccessToken, setError, setSuccessMessage }: RestoreBrowserTabProps) {
  const { tenants, loadingTenants } = useAdminConfig();
  const [selectedTenantId, setSelectedTenantId] = useState<string>("");
  // loadedTenantId pairs the displayed sessions with the tenant they belong to. The dropdown's
  // selectedTenantId can race ahead of an in-flight fetch; rendering only when the two match
  // prevents the operator from clicking a manifest that belongs to a different tenant than the
  // one currently selected. Codex P2.
  const [loadedTenantId, setLoadedTenantId] = useState<string>("");
  const [sessions, setSessions] = useState<SessionGroup[] | null>(null);
  const [loadingSessions, setLoadingSessions] = useState(false);
  const [refreshKey, setRefreshKey] = useState(0);
  const [expandedSessions, setExpandedSessions] = useState<Set<string>>(new Set());
  const [selection, setSelection] = useState<Selection | null>(null);
  const [restoring, setRestoring] = useState<Selection | null>(null);

  // "Only tenants with restore data" filter — off by default so the full AdminConfig tenant
  // list shows up immediately on first paint. Flipping it on fetches the cheap hierarchy
  // listing once and intersects the dropdown with the set of tenants that have ≥1 snapshot
  // blob. The fetched set is cached for the page lifetime; toggling off + on doesn't refetch.
  const [onlyWithRestore, setOnlyWithRestore] = useState(false);
  const [tenantsWithRestore, setTenantsWithRestore] = useState<Set<string> | null>(null);
  const [loadingRestoreFilter, setLoadingRestoreFilter] = useState(false);

  useEffect(() => {
    if (!onlyWithRestore || tenantsWithRestore !== null) return;
    const controller = new AbortController();
    let cancelled = false;
    setLoadingRestoreFilter(true);
    (async () => {
      try {
        const resp = await authenticatedFetch(
          api.sessionDeletions.tenantsWithManifests(),
          getAccessToken,
          { signal: controller.signal },
        );
        if (cancelled) return;
        if (!resp.ok) {
          const detail = await resp.text().catch(() => "");
          throw new Error(`HTTP ${resp.status}${detail ? ` — ${detail.slice(0, 200)}` : ""}`);
        }
        const json = (await resp.json()) as TenantsWithManifestsResponse;
        if (cancelled) return;
        setTenantsWithRestore(new Set(json.tenantIds ?? []));
      } catch (err) {
        if (cancelled) return;
        if (err instanceof DOMException && err.name === "AbortError") return;
        if (err instanceof TokenExpiredError) {
          setError("Session expired; reload the page and try again.");
        } else {
          setError(err instanceof Error ? err.message : String(err));
        }
        // On fetch failure leave the filter unchecked so the operator isn't stranded with an
        // empty dropdown — the toggle won't visually persist as "on" without data backing it.
        setOnlyWithRestore(false);
      } finally {
        if (!cancelled) setLoadingRestoreFilter(false);
      }
    })();
    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [onlyWithRestore, tenantsWithRestore, getAccessToken, setError]);

  // Sort tenants alphabetically by display name (domain fallback to id) so the dropdown is
  // operator-friendly. When the "only with restore" filter is active and the set has loaded,
  // intersect against it before sorting.
  const sortedTenants = useMemo(() => {
    const filtered = onlyWithRestore && tenantsWithRestore !== null
      ? tenants.filter((t) => tenantsWithRestore.has(t.tenantId))
      : tenants;
    return [...filtered].sort((a, b) => {
      const aLabel = (a.domainName || a.tenantId).toLowerCase();
      const bLabel = (b.domainName || b.tenantId).toLowerCase();
      return aLabel.localeCompare(bLabel);
    });
  }, [tenants, onlyWithRestore, tenantsWithRestore]);

  // Effect-scoped fetch (no useCallback) so each render captures the exact tenantId+refreshKey
  // snapshot — the AbortController + the loadedTenantId guard together prevent any out-of-order
  // response from clobbering newer data, and the immediate state-clear on tenant change means
  // the operator never sees stale manifests / preview / restore-action that belong to the
  // previous tenant. Codex P2.
  useEffect(() => {
    // Immediate clear: dropping the displayed data on switch means the operator never has a
    // "this manifest from tenant A is selected while the dropdown says tenant B" moment.
    setSessions(null);
    setExpandedSessions(new Set());
    setSelection(null);
    setLoadedTenantId("");

    if (!selectedTenantId) {
      setLoadingSessions(false);
      return;
    }

    const controller = new AbortController();
    let cancelled = false;
    setLoadingSessions(true);

    (async () => {
      try {
        const resp = await authenticatedFetch(
          api.sessionDeletions.tenantManifests(selectedTenantId),
          getAccessToken,
          { signal: controller.signal },
        );
        if (cancelled) return;
        if (!resp.ok) {
          const detail = await resp.text().catch(() => "");
          throw new Error(`HTTP ${resp.status}${detail ? ` — ${detail.slice(0, 200)}` : ""}`);
        }
        const json = (await resp.json()) as TenantManifestsResponse;
        if (cancelled) return;
        // Pin the response to the tenantId it was fetched for. The render path keys off
        // loadedTenantId === selectedTenantId, so a late response that no longer matches the
        // current dropdown is invisible to the operator even if it sneaks through.
        setLoadedTenantId(selectedTenantId);
        setSessions(json.sessions ?? []);
      } catch (err) {
        if (cancelled) return;
        // AbortError is the expected outcome when the effect re-runs before the previous fetch
        // resolved (tenant switched, refresh hit, component unmounted) — swallow silently.
        if (err instanceof DOMException && err.name === "AbortError") return;
        if (err instanceof TokenExpiredError) {
          setError("Session expired; reload the page and try again.");
        } else {
          setError(err instanceof Error ? err.message : String(err));
        }
        setSessions([]);
        setLoadedTenantId(selectedTenantId);
      } finally {
        if (!cancelled) setLoadingSessions(false);
      }
    })();

    return () => {
      cancelled = true;
      controller.abort();
    };
  }, [selectedTenantId, refreshKey, getAccessToken, setError]);

  // When the displayed data is for a different tenant than the dropdown shows (in-flight fetch),
  // treat sessions as not-yet-loaded for rendering purposes. Avoids briefly showing the previous
  // tenant's tree while the new one loads.
  const tenantMatches = loadedTenantId === selectedTenantId && selectedTenantId !== "";
  const visibleSessions = tenantMatches ? sessions : null;

  const toggleSession = (sessionId: string) => {
    setExpandedSessions((prev) => {
      const next = new Set(prev);
      if (next.has(sessionId)) next.delete(sessionId);
      else next.add(sessionId);
      return next;
    });
  };

  const expandAll = () => {
    if (visibleSessions == null) return;
    setExpandedSessions(new Set(visibleSessions.map((s) => s.sessionId)));
  };
  const collapseAll = () => setExpandedSessions(new Set());

  return (
    <>
      <div className="space-y-4">
        <div className="bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg p-4">
          <h2 className="text-lg font-medium text-gray-900 dark:text-white mb-1">Restore Browser</h2>
          <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">
            Pick a tenant to enumerate every persisted cascade-delete snapshot (33-day retention).
            Useful when the cascade already completed and the session no longer shows up on the
            In-Flight / Poisoned / Stranded tabs but the operator needs to recover it from the
            snapshot. Click a manifest on the left to preview, then <strong>Restore…</strong> to
            open the dry-run dialog with the IDs pre-filled.
          </p>

          <div className="flex items-end gap-3">
            <div className="flex-grow">
              <label className="block text-sm font-medium text-gray-700 dark:text-gray-200 mb-1">Tenant</label>
              <select
                value={selectedTenantId}
                onChange={(e) => setSelectedTenantId(e.target.value)}
                disabled={loadingTenants || loadingRestoreFilter}
                className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded text-sm text-gray-900 dark:text-white bg-white dark:bg-gray-900 focus:outline-none focus:ring-2 focus:ring-purple-500 disabled:opacity-60"
              >
                <option value="">
                  {loadingRestoreFilter ? "— loading tenants with restore data… —" : "— select a tenant —"}
                </option>
                {sortedTenants.map((t) => (
                  <option key={t.tenantId} value={t.tenantId}>
                    {t.domainName ? `${t.domainName} — ${t.tenantId}` : t.tenantId}
                  </option>
                ))}
              </select>
            </div>
            <button
              onClick={() => setRefreshKey((k) => k + 1)}
              disabled={!selectedTenantId || loadingSessions}
              className="px-3 py-2 text-sm border border-gray-300 dark:border-gray-600 rounded text-gray-700 dark:text-gray-200 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50 whitespace-nowrap"
            >
              {loadingSessions ? "Loading…" : "Refresh"}
            </button>
          </div>
          <div className="mt-3 flex items-center gap-2">
            <input
              id="restore-browser-only-with-data"
              type="checkbox"
              checked={onlyWithRestore}
              onChange={(e) => {
                const next = e.target.checked;
                setOnlyWithRestore(next);
                // If the filter excludes the currently-selected tenant, clear the selection so
                // the dropdown doesn't display a value that's no longer in the option list.
                if (next && tenantsWithRestore !== null && selectedTenantId
                    && !tenantsWithRestore.has(selectedTenantId)) {
                  setSelectedTenantId("");
                }
              }}
              className="h-4 w-4 rounded border-gray-300 dark:border-gray-600 text-purple-600 focus:ring-purple-500"
            />
            <label
              htmlFor="restore-browser-only-with-data"
              className="text-sm text-gray-700 dark:text-gray-200 select-none cursor-pointer"
            >
              Only tenants with restore data
              {onlyWithRestore && tenantsWithRestore !== null && (
                <span className="ml-2 text-xs text-gray-500 dark:text-gray-400">
                  ({sortedTenants.length} of {tenants.length})
                </span>
              )}
            </label>
          </div>
        </div>

        {selectedTenantId && (
          <div className="grid grid-cols-1 lg:grid-cols-5 gap-4">
            {/* Left: session/manifest tree */}
            <div className="lg:col-span-2 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg">
              <div className="px-4 py-3 border-b border-gray-200 dark:border-gray-700 flex items-center justify-between gap-2">
                <h3 className="text-sm font-medium text-gray-900 dark:text-white">
                  Sessions
                  {visibleSessions && (
                    <span className="ml-2 text-xs text-gray-500 dark:text-gray-400 font-normal">
                      ({visibleSessions.length} session{visibleSessions.length === 1 ? "" : "s"},{" "}
                      {visibleSessions.reduce((sum, s) => sum + s.manifestCount, 0)} manifest
                      {visibleSessions.reduce((sum, s) => sum + s.manifestCount, 0) === 1 ? "" : "s"})
                    </span>
                  )}
                </h3>
                <div className="flex gap-1">
                  <button
                    onClick={expandAll}
                    disabled={!visibleSessions || visibleSessions.length === 0}
                    className="text-xs px-2 py-1 border border-gray-300 dark:border-gray-600 rounded text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50"
                  >
                    Expand all
                  </button>
                  <button
                    onClick={collapseAll}
                    disabled={!visibleSessions || visibleSessions.length === 0}
                    className="text-xs px-2 py-1 border border-gray-300 dark:border-gray-600 rounded text-gray-600 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-700 disabled:opacity-50"
                  >
                    Collapse all
                  </button>
                </div>
              </div>
              <div className="max-h-[60vh] overflow-y-auto">
                {loadingSessions && (
                  <p className="px-4 py-4 text-sm text-gray-500 dark:text-gray-400">Loading manifests…</p>
                )}
                {!loadingSessions && visibleSessions && visibleSessions.length === 0 && (
                  <p className="px-4 py-4 text-sm text-gray-500 dark:text-gray-400">
                    No stored manifests for this tenant. Either no cascades have run, or all blobs have aged out of the
                    33-day retention window.
                  </p>
                )}
                {!loadingSessions && visibleSessions && visibleSessions.length > 0 && (
                  <ul className="divide-y divide-gray-100 dark:divide-gray-700 text-sm">
                    {visibleSessions.map((s) => {
                      const expanded = expandedSessions.has(s.sessionId);
                      const sessionAgeMin = ageMinutes(s.latestManifestUtc);
                      return (
                        <li key={s.sessionId}>
                          <button
                            onClick={() => toggleSession(s.sessionId)}
                            className="w-full flex items-center justify-between px-4 py-2 hover:bg-gray-50 dark:hover:bg-gray-700 text-left"
                          >
                            <span className="flex items-center gap-2 min-w-0">
                              <svg
                                className={`w-3 h-3 text-gray-400 transition-transform shrink-0 ${expanded ? "rotate-90" : ""}`}
                                fill="none"
                                stroke="currentColor"
                                viewBox="0 0 24 24"
                              >
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                              </svg>
                              <span className="font-mono text-xs text-gray-800 dark:text-gray-200 truncate">{s.sessionId}</span>
                              <span className="text-xs text-gray-400 dark:text-gray-500 whitespace-nowrap">
                                · {s.manifestCount} manifest{s.manifestCount === 1 ? "" : "s"}
                              </span>
                            </span>
                            <span className="text-xs text-gray-500 dark:text-gray-400 whitespace-nowrap ml-2">
                              {formatAge(sessionAgeMin)}
                            </span>
                          </button>
                          {expanded && (
                            <ul className="bg-gray-50 dark:bg-gray-900/50">
                              {s.manifests.map((m) => {
                                const isSelected =
                                  selection != null &&
                                  selection.tenantId === selectedTenantId &&
                                  selection.sessionId === s.sessionId &&
                                  selection.manifestId === m.manifestId;
                                return (
                                  <li key={m.manifestId}>
                                    <button
                                      onClick={() =>
                                        setSelection({
                                          tenantId: selectedTenantId,
                                          sessionId: s.sessionId,
                                          manifestId: m.manifestId,
                                        })
                                      }
                                      className={`w-full flex items-center justify-between gap-2 pl-10 pr-4 py-2 text-left text-xs hover:bg-purple-50 dark:hover:bg-purple-900/30 ${
                                        isSelected
                                          ? "bg-purple-100 dark:bg-purple-900/50 text-purple-900 dark:text-purple-100 font-medium"
                                          : "text-gray-700 dark:text-gray-300"
                                      }`}
                                    >
                                      <span className="font-mono truncate">{m.manifestId}</span>
                                      <span className="text-gray-500 dark:text-gray-400 whitespace-nowrap">
                                        {formatBytes(m.sizeBytes)} · {formatAge(ageMinutes(m.lastModifiedUtc))}
                                      </span>
                                    </button>
                                  </li>
                                );
                              })}
                            </ul>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                )}
              </div>
            </div>

            {/* Right: preview + restore action */}
            <div className="lg:col-span-3 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-lg">
              <div className="px-4 py-3 border-b border-gray-200 dark:border-gray-700 flex items-center justify-between gap-2">
                <div className="min-w-0">
                  <h3 className="text-sm font-medium text-gray-900 dark:text-white">Preview</h3>
                  {selection && (
                    <p className="text-xs text-gray-500 dark:text-gray-400 font-mono truncate mt-0.5">
                      Session {selection.sessionId}
                    </p>
                  )}
                </div>
                {selection && (
                  <button
                    onClick={() => setRestoring(selection)}
                    className="px-3 py-1 text-xs bg-amber-600 text-white rounded hover:bg-amber-700 shrink-0"
                  >
                    Restore…
                  </button>
                )}
              </div>
              <div className="px-4 py-4">
                {!selection && (
                  <p className="text-sm text-gray-500 dark:text-gray-400">
                    Select a manifest on the left to inspect its preflight counts, worker progress, and
                    last failure (if any). The <strong>Restore…</strong> button opens the same dry-run + restore dialog used by the Poisoned tab.
                  </p>
                )}
                {selection && (
                  <DeletionManifestSummaryView
                    tenantId={selection.tenantId}
                    sessionId={selection.sessionId}
                    manifestId={selection.manifestId}
                    getAccessToken={getAccessToken}
                    showDownload
                  />
                )}
              </div>
            </div>
          </div>
        )}
      </div>

      {restoring && (
        <RestoreConfirmDialog
          tenantId={restoring.tenantId}
          sessionId={restoring.sessionId}
          manifestId={restoring.manifestId}
          onClose={() => setRestoring(null)}
          onRestored={() => {
            setSuccessMessage(`Session ${restoring.sessionId} restore submitted.`);
            setRefreshKey((k) => k + 1);
          }}
          getAccessToken={getAccessToken}
        />
      )}
    </>
  );
}

function ageMinutes(iso: string): number {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return 0;
  return Math.max(0, Math.floor((Date.now() - then) / 60_000));
}

function formatAge(minutes: number): string {
  if (minutes < 60) return `${minutes}m ago`;
  if (minutes < 60 * 24) return `${Math.floor(minutes / 60)}h ago`;
  const days = Math.floor(minutes / (60 * 24));
  return `${days}d ago`;
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
}
