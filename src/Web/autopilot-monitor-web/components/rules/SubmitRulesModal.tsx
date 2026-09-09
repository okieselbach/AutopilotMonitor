"use client";

import { useState } from "react";
import Link from "next/link";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, jsonBody } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import { ModalPortal } from "@/components/ModalPortal";
import type {
  RuleSubmissionItem,
  SubmitRuleSubmissionsRequest,
  SubmitRuleSubmissionsResponse,
} from "@/utils/wire-types.generated";

/** The rule kinds a submission can carry (mirrors C# RuleSubmissionKinds). */
export type SubmittableRuleKind = "gather" | "analyze";

/** The subset of a rule the picker needs. */
export interface SubmittableRule {
  ruleId: string;
  title: string;
  category: string;
}

interface SubmitRulesModalProps {
  show: boolean;
  kind: SubmittableRuleKind;
  /** The tenant's own custom rules of this kind (never built-in or community). */
  rules: SubmittableRule[];
  /** Body tenant — the JWT tenant, or the foreign tenant in a Global Admin override. */
  tenantId: string;
  /** Prefill for the personal credit; the submitter may edit it. */
  defaultCreditName: string;
  getAccessToken: () => Promise<string | null>;
  onClose: () => void;
  /** Called after a successful submit so the page can refresh its submissions list. */
  onSubmitted: (created: RuleSubmissionItem[]) => void;
}

type AttributionMode = "anonymous" | "organization" | "person";

const MAX_ITEMS = 10;

/**
 * Picks one or more of the tenant's custom rules and submits them for the community pool. The
 * body carries rule references only — the backend freezes the rules — plus the comment and the
 * attribution choice, which is anonymous unless the submitter says otherwise.
 */
export function SubmitRulesModal({
  show, kind, rules, tenantId, defaultCreditName, getAccessToken, onClose, onSubmitted,
}: SubmitRulesModalProps) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [comment, setComment] = useState("");
  const [email, setEmail] = useState("");
  const [attributionMode, setAttributionMode] = useState<AttributionMode>("anonymous");
  const [creditName, setCreditName] = useState(defaultCreditName);
  const [consent, setConsent] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<RuleSubmissionItem[] | null>(null);

  // Reset the form each time the modal opens (adjust-during-render, no effect).
  const [prevShow, setPrevShow] = useState(show);
  if (prevShow !== show) {
    setPrevShow(show);
    if (show) {
      setSelected(new Set());
      setComment("");
      setEmail("");
      setAttributionMode("anonymous");
      setCreditName(defaultCreditName);
      setConsent(false);
      setError(null);
      setResult(null);
    }
  }

  if (!show) return null;

  const toggle = (ruleId: string) => {
    setError(null);
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(ruleId)) next.delete(ruleId);
      else if (next.size < MAX_ITEMS) next.add(ruleId);
      return next;
    });
  };

  const canSubmit = selected.size > 0 && consent && !submitting
    && (attributionMode !== "person" || creditName.trim().length > 0);

  const handleSubmit = async () => {
    try {
      setSubmitting(true);
      setError(null);
      const data = await fetchJson<SubmitRuleSubmissionsResponse>(
        api.ruleSubmissions.submit(),
        getAccessToken,
        {
          method: "POST",
          body: jsonBody<SubmitRuleSubmissionsRequest>({
            tenantId,
            items: Array.from(selected).map((ruleId) => ({ kind, ruleId })),
            comment: comment.trim() || undefined,
            email: email.trim() || undefined,
            attributionMode,
            attributionName: attributionMode === "person" ? creditName.trim() : undefined,
          }),
        },
      );
      trackEvent("rule_submission_submitted", { kind, count: data.submissions.length, attributionMode });
      setResult(data.submissions);
      onSubmitted(data.submissions);
    } catch (err) {
      setError(apiErrorText(err, "Failed to submit"));
    } finally {
      setSubmitting(false);
    }
  };

  const kindLabel = kind === "analyze" ? "analyze" : "gather";

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4" onClick={onClose}>
        <div
          className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto"
          onClick={(e) => e.stopPropagation()}
        >
          <div className="p-6">
            <div className="flex items-center mb-4">
              <div className="flex-shrink-0 w-12 h-12 bg-green-100 dark:bg-green-900/40 rounded-full flex items-center justify-center">
                <svg className="w-6 h-6 text-green-600 dark:text-green-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
                </svg>
              </div>
              <div className="ml-4">
                <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Contribute to the community</h3>
                <p className="text-xs text-gray-500 dark:text-gray-400">Custom {kindLabel} rules of this tenant</p>
              </div>
            </div>

            {result ? (
              <div>
                <div className="mb-4 p-4 bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg">
                  <p className="text-sm font-semibold text-green-800 dark:text-green-300">
                    {result.length === 1 ? "Rule submitted for review" : `${result.length} rules submitted for review`}
                  </p>
                  <p className="text-sm text-green-700 dark:text-green-400 mt-0.5">
                    You will see the decision on this page. Keep the submission id if you want to ask about it.
                  </p>
                </div>
                <ul className="space-y-2 text-sm">
                  {result.map((s) => (
                    <li key={s.submissionId} className="border border-gray-200 dark:border-gray-700 rounded-md px-3 py-2">
                      <div className="flex items-center justify-between gap-2">
                        <span className="font-medium text-gray-900 dark:text-gray-100 truncate">{s.sourceRuleId}</span>
                        <span className="font-mono text-xs text-gray-600 dark:text-gray-300">{s.submissionId}</span>
                      </div>
                      {s.validationFindings.length > 0 && (
                        <ul className="mt-1 space-y-0.5">
                          {s.validationFindings.map((f, i) => (
                            <li key={i} className={`text-xs ${f.level === "warning" ? "text-amber-700 dark:text-amber-400" : "text-gray-500 dark:text-gray-400"}`}>
                              {f.message}
                            </li>
                          ))}
                        </ul>
                      )}
                    </li>
                  ))}
                </ul>
                <div className="mt-6 flex justify-end">
                  <button onClick={onClose} className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors text-sm">
                    Close
                  </button>
                </div>
              </div>
            ) : (
              <>
                <p className="text-sm text-gray-700 dark:text-gray-300 mb-4">
                  Submitted rules are reviewed by the Autopilot Monitor team and, if accepted, published as community
                  rules for every tenant. Your own copy stays untouched. A rule may be adapted before it ships.
                </p>

                {error && (
                  <div className="mb-4 p-3 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg text-sm text-red-700 dark:text-red-400 whitespace-pre-wrap">
                    {error}
                  </div>
                )}

                {/* Rule picker */}
                <div className="mb-4">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Rules <span className="text-gray-400">({selected.size} of {rules.length} selected, max {MAX_ITEMS})</span>
                  </label>
                  {rules.length === 0 ? (
                    <p className="text-sm text-gray-500 dark:text-gray-400 italic">This tenant has no custom {kindLabel} rules yet.</p>
                  ) : (
                    <div className="max-h-48 overflow-y-auto border border-gray-200 dark:border-gray-700 rounded-md divide-y divide-gray-100 dark:divide-gray-700">
                      {rules.map((r) => (
                        <label key={r.ruleId} className="flex items-center gap-3 px-3 py-2 cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-700/50">
                          <input
                            type="checkbox"
                            checked={selected.has(r.ruleId)}
                            onChange={() => toggle(r.ruleId)}
                            disabled={submitting}
                            className="h-4 w-4 text-green-600 border-gray-300 rounded focus:ring-green-500"
                          />
                          <span className="min-w-0 flex-1">
                            <span className="block text-sm text-gray-900 dark:text-gray-100 truncate">{r.title}</span>
                            <span className="block text-xs font-mono text-gray-500 dark:text-gray-400">{r.ruleId} · {r.category}</span>
                          </span>
                        </label>
                      ))}
                    </div>
                  )}
                </div>

                {/* Comment */}
                <div className="mb-4">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Comment <span className="text-gray-400">(optional)</span>
                  </label>
                  <textarea
                    value={comment}
                    onChange={(e) => setComment(e.target.value)}
                    placeholder="What does the rule detect, and why is it useful for other organizations?"
                    className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                    rows={3}
                    maxLength={4000}
                    disabled={submitting}
                  />
                </div>

                {/* Contact email — a UPN is not always a mailbox */}
                <div className="mb-4">
                  <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Communication Email <span className="text-gray-400">(optional but recommended)</span>
                  </label>
                  <input
                    type="email"
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    placeholder="your.email@company.com"
                    maxLength={254}
                    className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                    disabled={submitting}
                  />
                  <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    Only for questions about this submission; never published. The decision itself appears on this page.
                  </p>
                </div>

                {/* Attribution */}
                <fieldset className="mb-4">
                  <legend className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Credit in the published rule</legend>
                  <div className="space-y-1.5 text-sm">
                    <label className="flex items-start gap-2 cursor-pointer">
                      <input type="radio" name="attribution" checked={attributionMode === "anonymous"} onChange={() => setAttributionMode("anonymous")} disabled={submitting} className="mt-0.5 h-4 w-4 text-green-600 border-gray-300 focus:ring-green-500" />
                      <span className="text-gray-800 dark:text-gray-200">Community contribution <span className="text-gray-400">(anonymous, default)</span></span>
                    </label>
                    <label className="flex items-start gap-2 cursor-pointer">
                      <input type="radio" name="attribution" checked={attributionMode === "organization"} onChange={() => setAttributionMode("organization")} disabled={submitting} className="mt-0.5 h-4 w-4 text-green-600 border-gray-300 focus:ring-green-500" />
                      <span className="text-gray-800 dark:text-gray-200">My organization <span className="text-gray-400">(the company name from the tenant settings)</span></span>
                    </label>
                    <label className="flex items-start gap-2 cursor-pointer">
                      <input type="radio" name="attribution" checked={attributionMode === "person"} onChange={() => setAttributionMode("person")} disabled={submitting} className="mt-0.5 h-4 w-4 text-green-600 border-gray-300 focus:ring-green-500" />
                      <span className="text-gray-800 dark:text-gray-200">A name of my choice</span>
                    </label>
                    {attributionMode === "person" && (
                      <input
                        type="text"
                        value={creditName}
                        onChange={(e) => setCreditName(e.target.value)}
                        maxLength={64}
                        placeholder="Name shown as the rule's author"
                        className="ml-6 w-[calc(100%-1.5rem)] px-3 py-1.5 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                        disabled={submitting}
                      />
                    )}
                  </div>
                  <p className="text-xs text-gray-500 dark:text-gray-400 mt-1.5">
                    The credit becomes the public <code className="text-xs">author</code> field of the rule and is visible to every tenant.
                  </p>
                </fieldset>

                {/* Consent */}
                <label className="flex items-start gap-2 text-sm cursor-pointer mb-2">
                  <input type="checkbox" checked={consent} onChange={(e) => setConsent(e.target.checked)} disabled={submitting} className="mt-0.5 h-4 w-4 text-green-600 border-gray-300 rounded focus:ring-green-500" />
                  <span className="text-gray-700 dark:text-gray-300">
                    I grant the non-exclusive, royalty-free right to include these detection definitions in the shared
                    community rule pool as described in the{" "}
                    <Link href="/terms" target="_blank" className="underline hover:text-gray-900 dark:hover:text-gray-100">Terms</Link>.
                  </span>
                </label>

                <div className="mt-6 flex items-center justify-end gap-2">
                  <button onClick={onClose} disabled={submitting} className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors text-sm">
                    Cancel
                  </button>
                  <button
                    onClick={handleSubmit}
                    disabled={!canSubmit}
                    className="px-4 py-2 bg-green-600 hover:bg-green-700 disabled:bg-green-400 disabled:cursor-not-allowed text-white rounded-md transition-colors text-sm font-medium"
                  >
                    {submitting ? "Submitting..." : selected.size > 1 ? `Submit ${selected.size} rules` : "Submit rule"}
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
