"use client";

import { Suspense, useCallback, useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, jsonBody, nullOn404 } from "@/lib/apiClient";
import TruncatedLabel from "@/components/TruncatedLabel";
import { extractContinuation } from "@/lib/paginationLink";
import { isGuid } from "@/utils/inputValidation";
import { trackEvent } from "@/lib/appInsights";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";
import { TableSkeleton } from "@/components/skeletons/TableSkeleton";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { ModalPortal } from "@/components/ModalPortal";
import { submissionStatusBadge } from "@/components/rules/MySubmissionsList";
import type {
  ReseedFromGitHubResponse,
  ReviewRuleSubmissionRequest,
  ReviewRuleSubmissionResponse,
  RuleSubmissionDetailResponse,
  RuleSubmissionFireStats,
  RuleSubmissionItem,
  RuleSubmissionListResponse,
} from "@/utils/wire-types.generated";

const PAGE_SIZE = 20;
const STATUS_FILTERS = ["pending", "approved", "declined", "withdrawn"] as const;

interface RuleSubmissionsSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

function CopyButton({ value, title = "Copy to clipboard" }: { value: string; title?: string }) {
  const [copied, setCopied] = useState(false);
  const handleCopy = (e: React.MouseEvent) => {
    e.stopPropagation();
    navigator.clipboard.writeText(value).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };
  return (
    <button onClick={handleCopy} title={title} className="ml-1.5 p-0.5 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-300 transition-colors">
      {copied ? (
        <svg className="w-3.5 h-3.5 text-green-500" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
        </svg>
      ) : (
        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z" />
        </svg>
      )}
    </button>
  );
}

function StatusBadge({ status }: { status: string }) {
  const b = submissionStatusBadge(status);
  return <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${b.cls}`}>{b.label}</span>;
}

function KindBadge({ kind }: { kind: string }) {
  const cls = kind === "analyze"
    ? "bg-indigo-100 text-indigo-800 dark:bg-indigo-900/40 dark:text-indigo-300"
    : "bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-300";
  return <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${cls}`}>{kind === "analyze" ? "Analyze" : "Gather"}</span>;
}

function fireStatsText(s: RuleSubmissionFireStats | undefined | null): string {
  if (!s) return "not available";
  return `fired ${s.fireCount}× in ${s.sessionsEvaluated} evaluated sessions (${s.days} days, ${s.evaluationCount} evaluations)`;
}

export function RuleSubmissionsSection(props: RuleSubmissionsSectionProps) {
  // useSearchParams() needs a Suspense boundary for the static prerender.
  return (
    <Suspense fallback={null}>
      <RuleSubmissionsSectionInner {...props} />
    </Suspense>
  );
}

function RuleSubmissionsSectionInner({ getAccessToken, setError }: RuleSubmissionsSectionProps) {
  // Decisions and the reseed are GA-only (GlobalAdminOnly routes); a Global Reader may look.
  const canMutate = useCanMutatePlatform();
  const [items, setItems] = useState<RuleSubmissionItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [detail, setDetail] = useState<RuleSubmissionDetailResponse | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);

  const [tenantFilterInput, setTenantFilterInput] = useState("");
  const [tenantFilterApplied, setTenantFilterApplied] = useState<string | undefined>(undefined);
  const [statusFilter, setStatusFilter] = useState<string>("");
  const [continuation, setContinuation] = useState<string | null>(null);
  const [nextLink, setNextLink] = useState<string | null>(null);
  const [continuationStack, setContinuationStack] = useState<Array<string | null>>([]);
  const [pageNumber, setPageNumber] = useState(1);

  const openDetail = useCallback(async (submissionId: string) => {
    try {
      setDetailLoading(true);
      const data = await fetchJson<RuleSubmissionDetailResponse>(api.ruleSubmissions.detail(submissionId), getAccessToken);
      setDetail(data);
    } catch (err) {
      setError(apiErrorText(err, "Failed to load the submission"));
    } finally {
      setDetailLoading(false);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setError]);

  // Deep link from the GA notification: ?submissionId=… opens the detail straight from the
  // detail endpoint — no dependency on the row being on the first page.
  const searchParams = useSearchParams();
  const submissionIdParam = searchParams?.get("submissionId") ?? null;
  const autoOpenConsumed = useRef(false);
  useEffect(() => {
    if (autoOpenConsumed.current || !submissionIdParam) return;
    autoOpenConsumed.current = true;
    const run = async () => { await openDetail(submissionIdParam); };
    void run();
  }, [submissionIdParam, openDetail]);

  const fetchPage = useCallback(async (cursor: string | null, filterTenantId: string | undefined, status: string) => {
    try {
      setLoading(true);
      const data = await fetchJson<RuleSubmissionListResponse>(
        api.ruleSubmissions.list({ tenantId: filterTenantId, status: status || undefined, pageSize: PAGE_SIZE, continuation: cursor ?? undefined }),
        getAccessToken,
      ).catch(nullOn404);
      setItems(data?.submissions ?? []);
      setNextLink(data?.nextLink ?? null);
    } catch (err) {
      setError(apiErrorText(err, "Failed to load rule submissions"));
    } finally {
      setLoading(false);
    }
    // getAccessToken churns on every MSAL refresh — see SessionReportsSection.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [setError]);

  useEffect(() => {
    const run = async () => {
      setContinuation(null);
      setContinuationStack([]);
      setPageNumber(1);
      await fetchPage(null, tenantFilterApplied, statusFilter);
    };
    void run();
  }, [tenantFilterApplied, statusFilter, fetchPage]);

  const handleApplyTenantFilter = () => {
    const trimmed = tenantFilterInput.trim();
    if (trimmed && !isGuid(trimmed)) {
      setError("Tenant ID must be a valid GUID");
      return;
    }
    setError(null);
    setTenantFilterApplied(trimmed || undefined);
  };

  const handleNextPage = () => {
    const nextCont = extractContinuation(nextLink);
    if (!nextCont) return;
    setContinuationStack((stack) => [...stack, continuation]);
    setContinuation(nextCont);
    setPageNumber((n) => n + 1);
    void fetchPage(nextCont, tenantFilterApplied, statusFilter);
  };

  const handlePrevPage = () => {
    if (continuationStack.length === 0) return;
    const prev = continuationStack[continuationStack.length - 1];
    setContinuationStack((stack) => stack.slice(0, -1));
    setContinuation(prev ?? null);
    setPageNumber((n) => Math.max(1, n - 1));
    void fetchPage(prev ?? null, tenantFilterApplied, statusFilter);
  };

  const handleRefresh = () => fetchPage(continuation, tenantFilterApplied, statusFilter);

  const handleReviewed = (updated: RuleSubmissionItem) => {
    setItems((prev) => prev.map((s) => (s.submissionId === updated.submissionId ? updated : s)));
    void openDetail(updated.submissionId);
  };

  return (
    <div className="bg-gradient-to-br from-indigo-50 to-purple-50 dark:from-gray-800 dark:to-gray-800 border-2 border-indigo-300 dark:border-indigo-700 rounded-lg shadow-lg">
      <SectionCardHeader
        tone="adminIndigoPurple"
        iconPath="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z"
        title="Rule Submissions"
        subtitle="Custom rules submitted by Tenant Admins for the community pool"
      />

      {/* Filters + refresh */}
      <div className="px-6 pt-4 pb-2 flex items-center gap-2 flex-wrap">
        <span className="text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wide">Filter:</span>
        <input
          type="text"
          placeholder="Tenant ID (GUID)"
          value={tenantFilterInput}
          onChange={(e) => setTenantFilterInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") handleApplyTenantFilter(); }}
          className="w-72 px-2 py-1 text-xs font-mono border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-green-500 focus:border-green-500"
        />
        <button onClick={handleApplyTenantFilter} disabled={loading} className="px-2.5 py-1 text-xs font-medium rounded-md border border-indigo-300 dark:border-indigo-700 text-indigo-700 dark:text-indigo-300 hover:bg-indigo-100 dark:hover:bg-indigo-900/40 disabled:opacity-40 transition-colors">
          Apply
        </button>
        {tenantFilterApplied && (
          <button onClick={() => { setTenantFilterInput(""); setTenantFilterApplied(undefined); }} disabled={loading} className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 transition-colors">
            Clear
          </button>
        )}
        <select
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value)}
          className="px-2 py-1 text-xs border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-green-500 focus:border-green-500"
        >
          <option value="">All statuses</option>
          {STATUS_FILTERS.map((s) => <option key={s} value={s}>{submissionStatusBadge(s).label}</option>)}
        </select>
        <button onClick={handleRefresh} disabled={loading} className="ml-auto px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 transition-colors">
          {loading ? "Loading..." : "Refresh"}
        </button>
      </div>

      {/* Table */}
      <div className="p-6 pt-2">
        {loading && items.length === 0 ? (
          <TableSkeleton columns={8} rows={6} />
        ) : items.length === 0 ? (
          <div className="text-center py-8 text-gray-500 dark:text-gray-400">
            <p className="text-sm">No rule submissions{statusFilter ? ` with status "${statusFilter}"` : " yet"}.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  {["ID", "Date", "Kind", "Rule", "Tenant", "Submitted By", "Credit", "Status", "Comment"].map((h) => (
                    <th key={h} className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {items.map((s) => (
                  <tr key={s.submissionId} onClick={() => openDetail(s.submissionId)} className="hover:bg-indigo-50 dark:hover:bg-indigo-900/20 cursor-pointer transition-colors">
                    <td className="px-4 py-3 text-sm font-mono text-gray-900 dark:text-gray-100 whitespace-nowrap">
                      <span className="inline-flex items-center">{s.submissionId}<CopyButton value={s.submissionId} /></span>
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 whitespace-nowrap">{new Date(s.submittedAt).toLocaleString()}</td>
                    <td className="px-4 py-3 text-sm whitespace-nowrap"><KindBadge kind={s.ruleKind} /></td>
                    <td className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 max-w-xs">
                      <span className="font-mono text-xs">{s.sourceRuleId}</span>
                      <TruncatedLabel interactive={false} text={s.title} className="block text-xs text-gray-500 dark:text-gray-400" />
                    </td>
                    <td className="px-4 py-3 text-sm font-mono text-gray-700 dark:text-gray-300">{s.tenantId.slice(0, 8)}...</td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300">{s.submittedBy}</td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300">{s.attributionName}</td>
                    <td className="px-4 py-3 text-sm whitespace-nowrap"><StatusBadge status={s.status} /></td>
                    <td className="px-4 py-3 max-w-xs">
                      {s.comment
                        ? <TruncatedLabel interactive={false} text={s.comment} className="block text-sm text-gray-700 dark:text-gray-300" />
                        : <span className="text-sm text-gray-400 italic">no comment</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md">
              <span className="text-xs text-gray-500 dark:text-gray-400">{items.length} on page {pageNumber}{nextLink ? "" : " (last)"}</span>
              <div className="flex items-center gap-2">
                <button onClick={handlePrevPage} disabled={continuationStack.length === 0 || loading} className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors">Previous</button>
                <button onClick={handleNextPage} disabled={!nextLink || loading} className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors">Next</button>
              </div>
            </div>
          </div>
        )}
      </div>

      {(detail || detailLoading) && (
        <SubmissionDetailModal
          detail={detail}
          loading={detailLoading}
          canMutate={canMutate}
          getAccessToken={getAccessToken}
          onClose={() => setDetail(null)}
          onReviewed={handleReviewed}
          onReload={(id) => openDetail(id)}
        />
      )}
    </div>
  );
}

// ── Detail modal ──────────────────────────────────────────────────────────

function SubmissionDetailModal({
  detail, loading, canMutate, getAccessToken, onClose, onReviewed, onReload,
}: {
  detail: RuleSubmissionDetailResponse | null;
  loading: boolean;
  canMutate: boolean;
  getAccessToken: () => Promise<string | null>;
  onClose: () => void;
  onReviewed: (updated: RuleSubmissionItem) => void;
  onReload: (submissionId: string) => void;
}) {
  const s = detail?.submission ?? null;
  const [publishedRuleId, setPublishedRuleId] = useState("");
  const [reviewComment, setReviewComment] = useState("");
  const [willBeAdapted, setWillBeAdapted] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [message, setMessage] = useState<{ kind: "ok" | "error"; text: string } | null>(null);
  const [showRepoFile, setShowRepoFile] = useState(false);

  // Seed the decision form from the loaded submission (adjust-during-render on identity change).
  const [seededFor, setSeededFor] = useState<string | null>(null);
  if (s && seededFor !== s.submissionId + s.status) {
    setSeededFor(s.submissionId + s.status);
    setPublishedRuleId(s.publishedRuleId ?? detail?.suggestedPublishedRuleId ?? "");
    setReviewComment(s.reviewComment ?? "");
    setWillBeAdapted(s.willBeAdapted);
    setMessage(null);
  }

  const rule = detail?.analyzeRule ?? detail?.gatherRule ?? null;
  const ruleJson = rule ? JSON.stringify(rule, null, 2) : "";
  const decidable = !!s && (s.status === "pending" || s.status === "approved");

  const review = async (decision: "approve" | "decline") => {
    if (!s) return;
    try {
      setBusy(decision);
      setMessage(null);
      const data = await fetchJson<ReviewRuleSubmissionResponse>(
        api.ruleSubmissions.review(s.submissionId),
        getAccessToken,
        {
          method: "PATCH",
          body: jsonBody<ReviewRuleSubmissionRequest>({
            decision,
            reviewComment: reviewComment.trim() || undefined,
            willBeAdapted: decision === "approve" ? willBeAdapted : undefined,
            publishedRuleId: decision === "approve" ? publishedRuleId.trim() : undefined,
          }),
        },
      );
      trackEvent("rule_submission_reviewed", { decision, kind: s.ruleKind });
      setMessage({ kind: "ok", text: data.message });
      onReviewed(data.submission);
    } catch (err) {
      setMessage({ kind: "error", text: apiErrorText(err, "Review failed") });
    } finally {
      setBusy(null);
    }
  };

  const reseed = async () => {
    if (!s) return;
    try {
      setBusy("reseed");
      setMessage(null);
      const type = s.ruleKind === "analyze" ? "analyze" : "gather";
      const data = await fetchJson<ReseedFromGitHubResponse>(api.rules.reseedFromGitHub(type), getAccessToken, { method: "POST" });
      const counts = type === "analyze" ? data.analyze : data.gather;
      setMessage({ kind: "ok", text: `Reseeded ${type} rules from GitHub: ${counts?.written ?? 0} written, ${counts?.deleted ?? 0} deleted.` });
      onReload(s.submissionId);
    } catch (err) {
      setMessage({ kind: "error", text: apiErrorText(err, "Reseed failed") });
    } finally {
      setBusy(null);
    }
  };

  const downloadRepoFile = () => {
    if (!detail?.repoFile) return;
    const blob = new Blob([detail.repoFile.content], { type: "application/json" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = detail.repoFile.path.split("/").pop() ?? "rule.json";
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4" onClick={onClose}>
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-3xl w-full max-h-[90vh] overflow-y-auto" onClick={(e) => e.stopPropagation()}>
          <div className="p-6">
            {!s ? (
              <p className="text-sm text-gray-500 dark:text-gray-400">{loading ? "Loading submission..." : "Submission not found."}</p>
            ) : (
              <>
                <div className="flex items-center justify-between mb-4 gap-3">
                  <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Rule submission</h3>
                  <div className="flex items-center gap-2">
                    <StatusBadge status={s.status} />
                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-mono bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300">
                      {s.submissionId}<CopyButton value={s.submissionId} />
                    </span>
                  </div>
                </div>

                {message && (
                  <div className={`mb-4 p-3 rounded-lg text-sm ${message.kind === "ok" ? "bg-green-50 text-green-800 border border-green-200" : "bg-red-50 text-red-700 border border-red-200"}`}>
                    {message.text}
                  </div>
                )}

                <dl className="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-3 text-sm">
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Rule</dt>
                    <dd className="mt-0.5 text-gray-900 dark:text-gray-100"><KindBadge kind={s.ruleKind} /> <span className="font-mono">{s.sourceRuleId}</span> — {s.title} <span className="text-gray-500">({s.category})</span></dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Tenant</dt>
                    <dd className="mt-0.5 font-mono text-gray-900 dark:text-gray-100 flex items-center">{s.tenantId}<CopyButton value={s.tenantId} /></dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Submitted by</dt>
                    <dd className="mt-0.5 text-gray-900 dark:text-gray-100">{s.submittedByName} <span className="text-gray-500">({s.submittedBy})</span> · {new Date(s.submittedAt).toLocaleString()}</dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Credit in the published rule</dt>
                    <dd className="mt-0.5 text-gray-900 dark:text-gray-100">{s.attributionName} <span className="text-gray-500">({s.attributionMode})</span></dd>
                  </div>
                  <div className="sm:col-span-2">
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Comment</dt>
                    <dd className="mt-0.5 text-gray-900 dark:text-gray-100 whitespace-pre-wrap">{s.comment || <span className="text-gray-400 italic">no comment</span>}</dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Fire stats in the submitting tenant</dt>
                    <dd className="mt-0.5 text-gray-900 dark:text-gray-100 text-xs">
                      <div>At submit: {fireStatsText(s.sourceFireStats)}</div>
                      <div>Now: {fireStatsText(detail?.liveFireStats)}</div>
                    </dd>
                  </div>
                  <div>
                    <dt className="font-medium text-gray-500 dark:text-gray-400">Pre-flight findings</dt>
                    <dd className="mt-0.5 text-xs">
                      {s.validationFindings.length === 0
                        ? <span className="text-green-700">none</span>
                        : <ul className="space-y-0.5">{s.validationFindings.map((f, i) => <li key={i} className={f.level === "warning" ? "text-amber-700" : "text-gray-600 dark:text-gray-300"}>{f.message}</li>)}</ul>}
                      {s.derivedFromTemplateRuleId && <div className="text-gray-500 mt-0.5">Derived from template <span className="font-mono">{s.derivedFromTemplateRuleId}</span></div>}
                    </dd>
                  </div>
                  {(s.reviewedBy || s.reviewComment) && (
                    <div className="sm:col-span-2">
                      <dt className="font-medium text-gray-500 dark:text-gray-400">Review</dt>
                      <dd className="mt-0.5 text-gray-900 dark:text-gray-100 text-xs">
                        {s.reviewedBy} · {s.reviewedAt ? new Date(s.reviewedAt).toLocaleString() : ""}
                        {s.publishedRuleId && <> · publishes as <span className="font-mono">{s.publishedRuleId}</span></>}
                        {s.willBeAdapted && <> · will be adapted</>}
                        {s.reviewComment && <div className="whitespace-pre-wrap mt-0.5">{s.reviewComment}</div>}
                      </dd>
                    </div>
                  )}
                </dl>

                {/* Frozen rule */}
                <div className="mt-4">
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium text-gray-500 dark:text-gray-400">Submitted rule (frozen at submit time)</span>
                    <CopyButton value={ruleJson} title="Copy rule JSON" />
                  </div>
                  <pre className="mt-1 max-h-64 overflow-auto text-xs font-mono bg-gray-50 dark:bg-gray-900 border border-gray-200 dark:border-gray-700 rounded p-3 text-gray-800 dark:text-gray-200">{ruleJson}</pre>
                </div>

                {/* Repo file */}
                {detail?.repoFile && (
                  <div className="mt-4">
                    <div className="flex items-center justify-between gap-2">
                      <span className="text-sm font-medium text-gray-500 dark:text-gray-400">
                        Repo file · <span className="font-mono text-xs">{detail.repoFile.path}</span>
                        {!s.publishedRuleId && detail.suggestedPublishedRuleId && <span className="text-xs text-gray-400"> (suggested id)</span>}
                      </span>
                      <span className="flex items-center gap-2">
                        <button onClick={() => setShowRepoFile((v) => !v)} className="text-xs text-indigo-600 hover:text-indigo-800">{showRepoFile ? "Hide" : "Show"}</button>
                        <CopyButton value={detail.repoFile.content} title="Copy file content" />
                        <button onClick={downloadRepoFile} className="text-xs text-indigo-600 hover:text-indigo-800">Download</button>
                      </span>
                    </div>
                    {showRepoFile && (
                      <pre className="mt-1 max-h-64 overflow-auto text-xs font-mono bg-gray-50 dark:bg-gray-900 border border-gray-200 dark:border-gray-700 rounded p-3 text-gray-800 dark:text-gray-200">{detail.repoFile.content}</pre>
                    )}
                  </div>
                )}

                {/* Decision */}
                {canMutate && decidable && (
                  <div className="mt-5 pt-4 border-t border-gray-200 dark:border-gray-700">
                    <p className="text-sm font-medium text-gray-700 dark:text-gray-300 mb-2">Decision</p>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                      <div>
                        <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Published rule id (approve)</label>
                        <input
                          type="text"
                          value={publishedRuleId}
                          onChange={(e) => setPublishedRuleId(e.target.value.toUpperCase())}
                          disabled={s.status !== "pending"}
                          className="w-full px-2 py-1.5 text-sm font-mono border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 disabled:opacity-60"
                        />
                        <label className="mt-2 flex items-center gap-2 text-xs text-gray-700 dark:text-gray-300">
                          <input type="checkbox" checked={willBeAdapted} onChange={(e) => setWillBeAdapted(e.target.checked)} disabled={s.status !== "pending"} className="h-4 w-4 text-green-600 border-gray-300 rounded" />
                          I will adapt the rule before publishing
                        </label>
                      </div>
                      <div>
                        <label className="block text-xs text-gray-500 dark:text-gray-400 mb-1">Note to the submitter (required to decline)</label>
                        <textarea
                          value={reviewComment}
                          onChange={(e) => setReviewComment(e.target.value)}
                          rows={3}
                          maxLength={4000}
                          className="w-full px-2 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100"
                        />
                      </div>
                    </div>
                    <div className="mt-3 flex items-center gap-2">
                      {s.status === "pending" && (
                        <button onClick={() => review("approve")} disabled={!!busy || !publishedRuleId.trim()} className="px-4 py-2 bg-green-600 hover:bg-green-700 disabled:bg-green-400 text-white rounded-md text-sm font-medium transition-colors">
                          {busy === "approve" ? "Approving..." : "Approve"}
                        </button>
                      )}
                      <button onClick={() => review("decline")} disabled={!!busy || !reviewComment.trim()} className="px-4 py-2 bg-red-600 hover:bg-red-700 disabled:bg-red-300 text-white rounded-md text-sm font-medium transition-colors">
                        {busy === "decline" ? "Declining..." : "Decline"}
                      </button>
                      {s.status === "approved" && (
                        <button onClick={reseed} disabled={!!busy} className="ml-auto px-4 py-2 bg-indigo-600 hover:bg-indigo-700 disabled:bg-indigo-400 text-white rounded-md text-sm font-medium transition-colors" title="After the repo file is committed and pushed: pull rules/dist from GitHub into the global catalog">
                          {busy === "reseed" ? "Reseeding..." : `Reseed ${s.ruleKind} rules now`}
                        </button>
                      )}
                    </div>
                  </div>
                )}

                <div className="mt-6 flex justify-end">
                  <button onClick={onClose} className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors text-sm">Close</button>
                </div>
              </>
            )}
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
