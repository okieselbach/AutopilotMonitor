"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, fetchOk, nullOn404 } from "@/lib/apiClient";
import type { RuleSubmissionItem, RuleSubmissionListResponse } from "@/utils/wire-types.generated";
import type { SubmittableRuleKind } from "./SubmitRulesModal";

interface MySubmissionsListProps {
  kind: SubmittableRuleKind;
  getAccessToken: () => Promise<string | null>;
  /** Bumped by the page after a submit so the list reloads. */
  refreshKey: number;
  /** Global Admin override: read the foreign tenant's submissions through the operator list. */
  overrideTenantId?: string;
  /** Read-only callers cannot withdraw. */
  readOnly: boolean;
  onError: (message: string) => void;
}

/** Status → badge classes from the existing palette; "published" reuses the Community green. */
export function submissionStatusBadge(status: string): { label: string; cls: string } {
  switch (status) {
    case "pending": return { label: "Pending review", cls: "bg-amber-100 text-amber-800" };
    case "approved": return { label: "Approved", cls: "bg-blue-100 text-blue-800" };
    case "published": return { label: "Published", cls: "bg-green-100 text-green-700" };
    case "declined": return { label: "Declined", cls: "bg-red-100 text-red-800" };
    case "withdrawn": return { label: "Withdrawn", cls: "bg-gray-100 text-gray-600" };
    default: return { label: status, cls: "bg-gray-100 text-gray-600" };
  }
}

/**
 * The tenant's own community submissions of one rule kind. Pending rows are the ones an admin
 * wants to see, so they show by default; decided and withdrawn rows sit behind "Show all". Renders
 * nothing while there is nothing to show, so pages without submissions stay unchanged. Closed rows
 * leave on their own: withdrawn after 30 days, declined after 90 (server-side retention).
 */
export function MySubmissionsList({ kind, getAccessToken, refreshKey, overrideTenantId, readOnly, onError }: MySubmissionsListProps) {
  const [items, setItems] = useState<RuleSubmissionItem[]>([]);
  const [collapsed, setCollapsed] = useState(false);
  const [showAll, setShowAll] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [withdrawing, setWithdrawing] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const url = overrideTenantId
        ? api.ruleSubmissions.list({ tenantId: overrideTenantId, pageSize: 200 })
        : api.ruleSubmissions.mine();
      const data = await fetchJson<RuleSubmissionListResponse>(url, getAccessToken).catch(nullOn404);
      setItems((data?.submissions ?? []).filter((s) => s.ruleKind === kind));
    } catch (err) {
      onError(apiErrorText(err, "Failed to load your community submissions"));
    }
    // getAccessToken churns on every MSAL refresh; the identity of onError is the page's setter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kind, overrideTenantId, refreshKey]);

  useEffect(() => {
    const run = async () => { await load(); };
    void run();
  }, [load]);

  const handleWithdraw = async (submissionId: string) => {
    try {
      setWithdrawing(submissionId);
      await fetchOk(api.ruleSubmissions.withdraw(submissionId), getAccessToken, { method: "DELETE" });
      await load();
    } catch (err) {
      onError(apiErrorText(err, "Failed to withdraw the submission"));
    } finally {
      setWithdrawing(null);
    }
  };

  if (items.length === 0) return null;

  const pendingItems = items.filter((s) => s.status === "pending");
  const pending = pendingItems.length;
  const visible = showAll ? items : pendingItems;
  const hidden = items.length - visible.length;

  return (
    <div className="bg-white rounded-lg shadow">
      <button
        onClick={() => setCollapsed((c) => !c)}
        className="w-full flex items-center justify-between px-4 py-3 text-left"
      >
        <span className="flex items-center gap-2 text-sm font-medium text-gray-900">
          <svg className={`w-4 h-4 text-gray-400 transition-transform ${collapsed ? "" : "rotate-90"}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
          </svg>
          Community submissions
          <span className="text-xs font-normal text-gray-500">({items.length}{pending > 0 ? `, ${pending} pending` : ""})</span>
        </span>
        {!collapsed && hidden > 0 && (
          <span
            role="button"
            onClick={(e) => { e.stopPropagation(); setShowAll(true); }}
            className="text-xs text-indigo-600 hover:text-indigo-800"
          >
            Show all ({hidden} more)
          </span>
        )}
        {!collapsed && showAll && items.length > pending && (
          <span
            role="button"
            onClick={(e) => { e.stopPropagation(); setShowAll(false); }}
            className="text-xs text-indigo-600 hover:text-indigo-800"
          >
            Pending only
          </span>
        )}
      </button>
      {!collapsed && visible.length === 0 && (
        <p className="border-t border-gray-100 px-4 py-2 text-xs text-gray-500">Nothing pending.</p>
      )}
      {!collapsed && visible.length > 0 && (
        <ul className="border-t border-gray-100 divide-y divide-gray-100">
          {visible.map((s) => {
            const badge = submissionStatusBadge(s.status);
            const open = expandedId === s.submissionId;
            const hasDetail = !!s.reviewComment || s.willBeAdapted || !!s.publishedRuleId;
            return (
              <li key={s.submissionId} className="px-4 py-2 text-sm">
                <div className="flex items-center gap-3 min-w-0">
                  <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium flex-shrink-0 ${badge.cls}`}>{badge.label}</span>
                  <span className="font-medium text-gray-900 truncate">{s.sourceRuleId}</span>
                  <span className="text-gray-500 truncate hidden sm:inline">{s.title}</span>
                  <span className="ml-auto font-mono text-xs text-gray-500 flex-shrink-0" title="Submission id">{s.submissionId}</span>
                  <span className="text-xs text-gray-400 flex-shrink-0 hidden md:inline">{new Date(s.submittedAt).toLocaleDateString()}</span>
                  {hasDetail && (
                    <button onClick={() => setExpandedId(open ? null : s.submissionId)} className="text-xs text-indigo-600 hover:text-indigo-800 flex-shrink-0">
                      {open ? "Hide" : "Details"}
                    </button>
                  )}
                  {!readOnly && s.status === "pending" && (
                    <button
                      onClick={() => handleWithdraw(s.submissionId)}
                      disabled={withdrawing === s.submissionId}
                      className="text-xs text-gray-500 hover:text-red-600 disabled:opacity-50 flex-shrink-0"
                    >
                      {withdrawing === s.submissionId ? "Withdrawing..." : "Withdraw"}
                    </button>
                  )}
                </div>
                {open && (
                  <div className="mt-2 ml-1 pl-3 border-l-2 border-gray-200 text-xs text-gray-700 space-y-1">
                    {s.publishedRuleId && (
                      <p>
                        {s.status === "published"
                          ? <>Published as <span className="font-mono">{s.publishedRuleId}</span> — the community rule is live for every tenant; you can delete your custom copy if you no longer need it.</>
                          : <>Will be published as <span className="font-mono">{s.publishedRuleId}</span>.</>}
                      </p>
                    )}
                    {s.willBeAdapted && <p>The published rule will be adapted for the community before it ships.</p>}
                    {s.reviewComment && <p className="whitespace-pre-wrap"><span className="text-gray-500">Reviewer:</span> {s.reviewComment}</p>}
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
