"use client";

import { useState } from "react";
import { api } from "@/lib/api";
import { apiErrorText, fetchOk, jsonBody } from "@/lib/apiClient";
import type { SubmitOffboardingFeedbackRequest } from "@/utils/wire-types.generated";

/** localStorage key prefix for "this user already submitted feedback for offboarding X". */
const FEEDBACK_SUBMITTED_KEY_PREFIX = "offboard-feedback-submitted:";

export const OFFBOARDING_FEEDBACK_MAX_CHARS = 4096;

interface OffboardingFeedbackFormProps {
  /** Tenant being offboarded — the feedback POST is scoped to it. */
  tenantId: string;
  /**
   * Identity of THIS offboarding for the "already submitted" memory: the History row key when
   * the caller has it (the banner right after the DELETE), else the tenant id (the suspended
   * page after a reload, which only knows the tenant). Keyed per offboarding so a tenant that
   * re-onboards and offboards again gets a fresh prompt.
   */
  offboardingKey: string;
  getAccessToken: () => Promise<string | null>;
}

/**
 * The farewell feedback textarea a departing tenant admin sees while the offboarding is queued.
 * Rendered twice: inside the drain-barrier banner right after the offboard click, and on the
 * suspended page a reload lands on during the same window — the offboard endpoint tombstones
 * the tenant before it answers, so any reload in those ~6 minutes would otherwise lose the form.
 * The backend accepts the POST only while the offboarding marker is Initiated/InProgress and
 * answers 404/409 afterwards; that error text is shown as-is.
 */
export default function OffboardingFeedbackForm({ tenantId, offboardingKey, getAccessToken }: OffboardingFeedbackFormProps) {
  const localStorageKey = `${FEEDBACK_SUBMITTED_KEY_PREFIX}${offboardingKey}`;
  const [feedbackText, setFeedbackText] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [alreadySubmitted, setAlreadySubmitted] = useState<boolean>(() => {
    if (typeof window === "undefined") return false;
    try {
      return window.localStorage.getItem(localStorageKey) === "1";
    } catch {
      return false;
    }
  });

  const handleSubmitFeedback = async () => {
    const trimmed = feedbackText.trim();
    if (trimmed.length === 0) return;

    setSubmitting(true);
    setSubmitError(null);
    try {
      await fetchOk(api.tenants.offboardFeedback(tenantId), getAccessToken, {
        method: "POST",
        body: jsonBody<SubmitOffboardingFeedbackRequest>({ comment: trimmed.slice(0, OFFBOARDING_FEEDBACK_MAX_CHARS) }),
      });
      try {
        window.localStorage.setItem(localStorageKey, "1");
      } catch {
        // localStorage disabled — UI still flips via state below; the round trip already succeeded.
      }
      setAlreadySubmitted(true);
    } catch (err) {
      // apiErrorText renders a token expiry centrally; the offboarding continues either way.
      setSubmitError(apiErrorText(err, "Failed to submit feedback"));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="bg-gray-50 border border-gray-200 rounded-lg p-4 space-y-3 text-left">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">We&apos;re sorry to see you go</h3>
        <p className="text-xs text-gray-600 mt-0.5">
          What could we have done better? Anything specific that didn&apos;t fit your needs?
          (optional, max {OFFBOARDING_FEEDBACK_MAX_CHARS} characters)
        </p>
      </div>

      {alreadySubmitted ? (
        <div className="flex items-center gap-2 text-sm text-green-700 bg-green-50 border border-green-200 rounded-md px-3 py-2">
          <svg className="w-4 h-4 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
            <path fillRule="evenodd" clipRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" />
          </svg>
          <span>Thanks for the feedback — it really helps shape what we build next.</span>
        </div>
      ) : (
        <>
          <textarea
            value={feedbackText}
            onChange={(e) => setFeedbackText(e.target.value.slice(0, OFFBOARDING_FEEDBACK_MAX_CHARS))}
            disabled={submitting}
            rows={4}
            placeholder="Pricing, missing features, bugs that bit us, alternatives we picked, …"
            className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 text-sm disabled:bg-gray-100"
          />
          <div className="flex items-center justify-between text-xs text-gray-500">
            <span>{feedbackText.length}/{OFFBOARDING_FEEDBACK_MAX_CHARS}</span>
            <button
              onClick={handleSubmitFeedback}
              disabled={submitting || feedbackText.trim().length === 0}
              className="px-3 py-1.5 bg-green-600 text-white rounded-md hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium"
            >
              {submitting ? "Submitting…" : "Submit feedback"}
            </button>
          </div>
          {submitError && (
            <p className="text-xs text-red-600">{submitError}</p>
          )}
        </>
      )}
    </div>
  );
}
