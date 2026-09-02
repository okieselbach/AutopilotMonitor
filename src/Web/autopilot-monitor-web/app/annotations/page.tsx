"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { sessionUrl } from "@/lib/routes";
import { scopedApi } from "@/lib/scopedApi";
import { API_BASE_URL } from "@/utils/config";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { ProtectedRoute } from "@/components/ProtectedRoute";
import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode, useAggregatedAdminScope } from "@/hooks";
import { TenantScopeSelector } from "@/components/TenantScopeSelector";
import { GlobalAdminBanner, globalAdminSubtitle } from "@/components/GlobalAdminBanner";
import {
  ANNOTATION_VERDICTS,
  LANE_LABELS,
  VERDICT_DESCRIPTIONS,
  VERDICT_LABELS,
  VERDICT_PILL_CLASSES,
  visibleLanes,
  type AnnotationLane,
  type AnnotationVerdict,
} from "../sessions/components/sessionAnnotationLogic";
import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";

/**
 * Annotations overview: every annotated session in one list, so a judged session can be
 * found again without remembering which one it was. Server-side filters (verdict, lane,
 * free-text note search) + nextLink pagination; rows deep-link into the session's annotation
 * section. The search is how a demo session that has fallen out of every time window is
 * found again: describe it once in a note ("wifi switch"), then search for that. The
 * backend excludes the platform-internal globaladmin lane for tenant callers.
 *
 * Cross-tenant: a GA/Reader gets the tenant switcher incl. the "All tenants" aggregate
 * (the same evaluation stream MCP's list_session_annotations serves); a delegated
 * ("MSP") admin gets the switcher bounded to its managed tenants.
 */

interface AnnotationRow {
  tenantId?: string | null;
  sessionId: string;
  lane: string;
  verdict?: string | null;
  note?: string | null;
  authorDisplayName?: string | null;
  updatedAtUtc?: string | null;
  ruleIds?: string[] | null;
}

interface AnnotationsListResponse {
  success: boolean;
  annotations?: AnnotationRow[] | null;
  nextLink?: string | null;
}

export default function AnnotationsPage() {
  const { getAccessToken, user } = useAuth();

  const [rows, setRows] = useState<AnnotationRow[]>([]);
  const [nextLink, setNextLink] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [verdictFilter, setVerdictFilter] = useState("");
  const [laneFilter, setLaneFilter] = useState("");
  // Free-text search over note + verdict, server-side. The input updates per keystroke;
  // the SUBMITTED value follows after a short pause so typing never fires a request per
  // key (the setState lives in the timer callback, not the effect body).
  const [noteQuery, setNoteQuery] = useState("");
  const [submittedQuery, setSubmittedQuery] = useState("");
  useEffect(() => {
    const trimmed = noteQuery.trim();
    const timer = setTimeout(() => setSubmittedQuery(trimmed), 300);
    return () => clearTimeout(timer);
  }, [noteQuery]);

  // GA/Reader: "All tenants" aggregate by default (mirrors the MCP evaluation stream) with
  // per-tenant drill-down; delegated ("MSP"): managed tenants only, never aggregated.
  const scope = useAggregatedAdminScope({ defaultAggregated: true });
  const { isGlobalAdmin: crossTenant, routeGlobal, selectedTenantId, effectiveTenantId, scopeInitialized, tenants } = scope;

  // Effective Global-Admin view (demo mode forces it off): the platform-internal lane follows the
  // view both in the filter dropdown and in the list, so presenting this page shows exactly the
  // rows a tenant admin would see. The backend filters the lane for tenant callers regardless.
  const { globalAdminMode } = useAdminMode();
  const lanes = visibleLanes(user, globalAdminMode);
  const visibleRows = rows.filter((r) => lanes.includes(r.lane?.toLowerCase() as AnnotationLane));

  const tenantLabel = (id: string | null | undefined) =>
    tenants.find((t) => t.tenantId === id)?.domainName ?? id ?? "—";

  const fetchPage = useCallback(
    async (url: string, append: boolean) => {
      try {
        const res = await authenticatedFetch(url, getAccessToken);
        if (res.status === 404) {
          // Deploy skew: a backend without the route yet. Reads as "nothing annotated",
          // never as an error — the empty state below carries the call to action.
          if (!append) setRows([]);
          setNextLink(null);
          return;
        }
        if (!res.ok) throw new Error(res.statusText);
        const json = (await res.json()) as AnnotationsListResponse;
        setRows((prev) => (append ? [...prev, ...(json.annotations ?? [])] : (json.annotations ?? [])));
        setNextLink(json.nextLink ?? null);
      } catch {
        // Non-technical by design — the status text means nothing to the reader.
        setLoadError("Annotations could not be loaded right now — please try again in a moment.");
      } finally {
        setLoading(false);
      }
    },
    [getAccessToken]
  );

  useEffect(() => {
    // Wait for the scope's default selection to settle so we never fire a request in the
    // wrong scope. Inner async wrapper: set-state-in-effect flags a direct call to a
    // state-setting callback even when every setState sits behind an await.
    if (!scopeInitialized) return;
    const loadFirstPage = async () => {
      setLoading(true);
      setLoadError(null);
      const filters = {
        verdict: verdictFilter || undefined,
        lane: laneFilter || undefined,
        q: submittedQuery || undefined,
      };
      await fetchPage(
        scopedApi.annotationsList({ routeGlobal, selectedTenantId, effectiveTenantId }, filters),
        false
      );
    };
    void loadFirstPage();
  }, [fetchPage, verdictFilter, laneFilter, submittedQuery, scopeInitialized, routeGlobal, selectedTenantId, effectiveTenantId]);

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <GlobalAdminBanner
          show={crossTenant}
          delegated={scope.isDelegatedScope}
          subtitle={globalAdminSubtitle(scope, "aggregating annotations across all tenants")}
        />
        <header className="bg-white shadow">
          <div className="py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex flex-wrap items-center justify-between gap-y-3">
              <div>
                <h1 className="text-2xl font-normal text-gray-900">Annotations</h1>
                <p className="mt-1 text-sm text-gray-500">
                  Every session your team has judged — open one to read or update the verdict.
                </p>
              </div>
              <div className="flex flex-wrap items-center gap-2">
                <TenantScopeSelector scope={scope} allowAggregated />
                <DocsLink path={DOCS_PATHS.annotations} label="Docs" />
              </div>
            </div>
          </div>
        </header>

        <main className="py-6 px-4 sm:px-6 lg:px-8">
          <div className="bg-white shadow rounded-lg p-6">
            <div className="flex flex-wrap items-center gap-3 mb-4">
              <select
                value={verdictFilter}
                onChange={(e) => setVerdictFilter(e.target.value)}
                className="border border-gray-300 rounded-md px-3 py-1.5 text-sm text-gray-700 focus:outline-none focus:ring-2 focus:ring-green-500"
                aria-label="Filter by verdict"
              >
                <option value="">All verdicts</option>
                {ANNOTATION_VERDICTS.map((v) => (
                  <option key={v} value={v}>{VERDICT_LABELS[v]}</option>
                ))}
              </select>
              <select
                value={laneFilter}
                onChange={(e) => setLaneFilter(e.target.value)}
                className="border border-gray-300 rounded-md px-3 py-1.5 text-sm text-gray-700 focus:outline-none focus:ring-2 focus:ring-green-500"
                aria-label="Filter by role"
              >
                <option value="">All roles</option>
                {lanes.map((lane) => (
                  <option key={lane} value={lane}>{LANE_LABELS[lane]}</option>
                ))}
              </select>
              <div className="relative flex-1 min-w-[220px] max-w-md">
                <input
                  type="search"
                  value={noteQuery}
                  onChange={(e) => setNoteQuery(e.target.value)}
                  placeholder="Search notes (e.g. wifi switch)"
                  maxLength={200}
                  aria-label="Search annotation notes"
                  className="w-full border border-gray-300 rounded-md px-3 py-1.5 pr-8 text-sm text-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500"
                />
                {noteQuery && (
                  <button
                    type="button"
                    onClick={() => setNoteQuery("")}
                    className="absolute inset-y-0 right-2 flex items-center text-gray-400 hover:text-gray-600"
                    aria-label="Clear search"
                    title="Clear search"
                  >
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                    </svg>
                  </button>
                )}
              </div>
            </div>

            {loadError ? (
              <p className="text-sm text-amber-700">{loadError}</p>
            ) : visibleRows.length === 0 && !loading ? (
              submittedQuery ? (
                <p className="text-sm text-gray-400">
                  No annotation mentions <span className="font-medium">&ldquo;{submittedQuery}&rdquo;</span>.
                </p>
              ) : (
                <p className="text-sm text-gray-400">
                  No annotations yet. Open a session and record a verdict in its{" "}
                  <span className="font-medium">Annotations</span> section — judged sessions
                  show up here.
                </p>
              )
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 text-sm">
                  <thead>
                    <tr className="text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                      {crossTenant && <th className="px-3 py-2">Tenant</th>}
                      <th className="px-3 py-2">Verdict</th>
                      <th className="px-3 py-2">Role</th>
                      <th className="px-3 py-2">Note</th>
                      <th className="px-3 py-2">Author</th>
                      <th className="px-3 py-2">Updated</th>
                      <th className="px-3 py-2"></th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100">
                    {visibleRows.map((row) => (
                      <tr key={`${row.tenantId ?? ""}_${row.sessionId}_${row.lane}`} className="hover:bg-gray-50">
                        {crossTenant && (
                          <td className="px-3 py-2 whitespace-nowrap text-gray-700">
                            {tenantLabel(row.tenantId)}
                          </td>
                        )}
                        <td className="px-3 py-2 whitespace-nowrap">
                          {row.verdict != null ? (
                            <span
                              title={VERDICT_DESCRIPTIONS[row.verdict as AnnotationVerdict]}
                              className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${VERDICT_PILL_CLASSES[row.verdict] ?? "bg-gray-100 text-gray-600"}`}
                            >
                              {VERDICT_LABELS[row.verdict as AnnotationVerdict] ?? row.verdict}
                            </span>
                          ) : (
                            <span className="text-xs text-gray-400">note only</span>
                          )}
                        </td>
                        <td className="px-3 py-2 whitespace-nowrap text-gray-700">
                          {LANE_LABELS[row.lane as AnnotationLane] ?? row.lane}
                        </td>
                        <td className="px-3 py-2 text-gray-700 max-w-md">
                          <span className="line-clamp-2">{row.note || "—"}</span>
                        </td>
                        <td className="px-3 py-2 whitespace-nowrap text-gray-500">
                          {row.authorDisplayName || "—"}
                        </td>
                        <td className="px-3 py-2 whitespace-nowrap text-gray-500">
                          {row.updatedAtUtc ? new Date(row.updatedAtUtc).toLocaleString() : "—"}
                        </td>
                        <td className="px-3 py-2 whitespace-nowrap">
                          <Link
                            href={sessionUrl(row.sessionId, {
                              tenantId: crossTenant ? row.tenantId || undefined : undefined,
                              hash: "section-annotations",
                            })}
                            className="text-green-700 hover:text-green-800 underline underline-offset-2"
                          >
                            open session
                          </Link>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            <div className="flex items-center gap-3 mt-4">
              {loading && <span className="text-sm text-gray-400">Loading…</span>}
              {!loading && nextLink && (
                <button
                  onClick={() => {
                    setLoading(true);
                    setLoadError(null);
                    fetchPage(`${API_BASE_URL}${nextLink}`, true);
                  }}
                  className="px-4 py-1.5 text-sm font-medium bg-white border border-gray-300 rounded-md text-gray-700 hover:bg-gray-50"
                >
                  Load more
                </button>
              )}
            </div>
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
