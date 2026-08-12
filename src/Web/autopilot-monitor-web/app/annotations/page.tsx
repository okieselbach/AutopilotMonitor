"use client";

import { useCallback, useEffect, useState } from "react";
import Link from "next/link";
import { sessionUrl } from "@/lib/routes";
import { api } from "@/lib/api";
import { API_BASE_URL } from "@/utils/config";
import { authenticatedFetch } from "@/lib/authenticatedFetch";
import { ProtectedRoute } from "@/components/ProtectedRoute";
import { useAuth } from "@/contexts/AuthContext";
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

/**
 * Annotations overview: every annotated session of the tenant in one list, so a judged
 * session can be found again without remembering which one it was. Server-side filters
 * (verdict, lane) + nextLink pagination; rows deep-link into the session's annotation
 * section. The backend excludes the platform-internal globaladmin lane for tenant callers.
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

  const lanes = visibleLanes(user);

  // Callers must set loading=true / loadError=null before invoking (the initial
  // state covers the mount fetch) — no synchronous setState here, this runs
  // inside the effect below.
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
    // Inner async wrapper: set-state-in-effect flags a direct call to a
    // state-setting callback even when every setState sits behind an await.
    const loadFirstPage = async () => {
      await fetchPage(
        api.annotations.list({
          verdict: verdictFilter || undefined,
          lane: laneFilter || undefined,
        }),
        false
      );
    };
    void loadFirstPage();
  }, [fetchPage, verdictFilter, laneFilter]);

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        <header className="bg-white shadow">
          <div className="py-6 px-4 sm:px-6 lg:px-8">
            <h1 className="text-2xl font-normal text-gray-900">Annotations</h1>
            <p className="mt-1 text-sm text-gray-500">
              Every session your team has judged — open one to read or update the verdict.
            </p>
          </div>
        </header>

        <main className="py-6 px-4 sm:px-6 lg:px-8">
          <div className="bg-white shadow rounded-lg p-6">
            <div className="flex flex-wrap items-center gap-3 mb-4">
              <select
                value={verdictFilter}
                onChange={(e) => {
                  setVerdictFilter(e.target.value);
                  setLoading(true);
                  setLoadError(null);
                }}
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
                onChange={(e) => {
                  setLaneFilter(e.target.value);
                  setLoading(true);
                  setLoadError(null);
                }}
                className="border border-gray-300 rounded-md px-3 py-1.5 text-sm text-gray-700 focus:outline-none focus:ring-2 focus:ring-green-500"
                aria-label="Filter by role"
              >
                <option value="">All roles</option>
                {lanes.map((lane) => (
                  <option key={lane} value={lane}>{LANE_LABELS[lane]}</option>
                ))}
              </select>
            </div>

            {loadError ? (
              <p className="text-sm text-amber-700">{loadError}</p>
            ) : rows.length === 0 && !loading ? (
              <p className="text-sm text-gray-400">
                No annotations yet. Open a session and record a verdict in its{" "}
                <span className="font-medium">Annotations</span> section — judged sessions
                show up here.
              </p>
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 text-sm">
                  <thead>
                    <tr className="text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                      <th className="px-3 py-2">Verdict</th>
                      <th className="px-3 py-2">Role</th>
                      <th className="px-3 py-2">Note</th>
                      <th className="px-3 py-2">Author</th>
                      <th className="px-3 py-2">Updated</th>
                      <th className="px-3 py-2"></th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-gray-100">
                    {rows.map((row) => (
                      <tr key={`${row.sessionId}_${row.lane}`} className="hover:bg-gray-50">
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
                            href={sessionUrl(row.sessionId, { hash: "section-annotations" })}
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
