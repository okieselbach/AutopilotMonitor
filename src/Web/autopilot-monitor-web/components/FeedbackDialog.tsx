"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { apiErrorText, fetchOk, jsonBody } from "@/lib/apiClient";
import { trackEvent } from "@/lib/appInsights";
import { closeFeedbackDialog } from "@/lib/feedbackDialogStore";
import { route } from "@/lib/routes";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import type { GeneralFeedbackRequest } from "@/utils/wire-types.generated";
import { ModalPortal } from "./ModalPortal";
import { NavLink } from "./NavLink";

const MAX_CHARS = SHARED_MANIFEST.submissionLimits.feedbackTextMaxChars;
const MAX_EMAIL_CHARS = SHARED_MANIFEST.submissionLimits.contactEmailMaxChars;

const FIELD_CLASS =
  "w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500";

/** Free-text feedback to the Autopilot Monitor team (POST feedback/general), opened from the help menu. */
export function FeedbackDialog({ source }: { source: string }) {
  const { user, getAccessToken } = useAuth();
  const [message, setMessage] = useState("");
  const [contactEmail, setContactEmail] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Logs and session reports are Admin/Operator routes; everyone else sees only the plain form.
  const canSubmitFiles = !!user && (user.isTenantAdmin || user.role === "Operator" || user.isGlobalAdmin);
  const trimmed = message.trim();

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape" && !submitting) closeFeedbackDialog();
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [submitting]);

  const handleSubmit = async () => {
    if (!trimmed || submitting) return;
    setSubmitting(true);
    setError(null);
    try {
      const email = contactEmail.trim();
      await fetchOk(api.feedback.general(), getAccessToken, {
        method: "POST",
        body: jsonBody<GeneralFeedbackRequest>({ message: trimmed, contactEmail: email || null }),
      });
      trackEvent("feedback_submitted", { source, hasContactEmail: email.length > 0 });
      setSent(true);
    } catch (err: unknown) {
      setError(apiErrorText(err, "Your feedback could not be sent."));
    } finally {
      setSubmitting(false);
    }
  };

  // A click beside the card only closes an empty or finished form, so typed text is never lost by accident.
  const closeFromBackdrop = () => {
    if (!submitting && (sent || trimmed.length === 0)) closeFeedbackDialog();
  };

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4" onClick={closeFromBackdrop}>
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="feedback-dialog-title"
          className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto"
          onClick={e => e.stopPropagation()}
        >
          <div className="p-6">
            <div className="flex items-center mb-4">
              <div className="flex-shrink-0 w-12 h-12 bg-blue-100 dark:bg-blue-900/40 rounded-full flex items-center justify-center">
                <svg className="w-6 h-6 text-blue-600 dark:text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
                </svg>
              </div>
              <h3 id="feedback-dialog-title" className="ml-4 text-lg font-semibold text-gray-900 dark:text-gray-100">
                Send feedback
              </h3>
            </div>

            {sent ? (
              <div className="mb-4 p-4 bg-green-50 dark:bg-green-900/20 border border-green-200 dark:border-green-800 rounded-lg flex items-start gap-3">
                <svg className="w-5 h-5 text-green-600 dark:text-green-400 flex-shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <p className="text-sm text-green-800 dark:text-green-300">Thanks — your feedback reached the team.</p>
              </div>
            ) : (
              <>
                <p className="text-sm text-gray-700 dark:text-gray-300 mb-4">
                  Ideas, problems or praise go straight to the Autopilot Monitor team. No GitHub account needed.
                </p>

                {error && (
                  <div className="mb-4 p-3 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg text-sm text-red-700 dark:text-red-300">
                    {error}
                  </div>
                )}

                <div className="mb-4">
                  <label htmlFor="feedback-message" className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Your feedback
                  </label>
                  <textarea
                    id="feedback-message"
                    value={message}
                    onChange={e => setMessage(e.target.value)}
                    maxLength={MAX_CHARS}
                    rows={5}
                    autoFocus
                    disabled={submitting}
                    placeholder="What works, what doesn't, what's missing?"
                    className={FIELD_CLASS}
                  />
                  {message.length > 0 && (
                    <p className="text-xs text-gray-400 text-right mt-0.5">{message.length} / {MAX_CHARS}</p>
                  )}
                </div>

                <div className="mb-4">
                  <label htmlFor="feedback-email" className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                    Reply address <span className="text-gray-400">(optional)</span>
                  </label>
                  <input
                    id="feedback-email"
                    type="email"
                    value={contactEmail}
                    onChange={e => setContactEmail(e.target.value)}
                    maxLength={MAX_EMAIL_CHARS}
                    disabled={submitting}
                    placeholder="your.email@company.com"
                    className={FIELD_CLASS}
                  />
                  <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
                    Leave an address if you would like an answer and your sign-in name is not a mailbox.
                  </p>
                </div>

                {canSubmitFiles && (
                  <div className="mb-6 rounded-md bg-gray-50 dark:bg-gray-700/50 px-3 py-2 text-sm text-gray-700 dark:text-gray-300 space-y-1">
                    <p>
                      Logs or screenshots to share?{" "}
                      <NavLink
                        href={route("/settings/tenant/support")}
                        onClick={() => {
                          trackEvent("submit_logs_opened", { source: "feedback_dialog" });
                          closeFeedbackDialog();
                        }}
                        className="font-medium text-blue-600 dark:text-blue-400 hover:underline"
                      >
                        Submit logs →
                      </NavLink>
                    </p>
                    <p>About one enrollment? Use Report Session on that session.</p>
                  </div>
                )}
              </>
            )}

            <div className="flex justify-end gap-3">
              {sent ? (
                <button
                  onClick={closeFeedbackDialog}
                  className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors"
                >
                  Close
                </button>
              ) : (
                <>
                  <button
                    onClick={closeFeedbackDialog}
                    disabled={submitting}
                    className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors disabled:opacity-50"
                  >
                    Cancel
                  </button>
                  <button
                    onClick={handleSubmit}
                    disabled={submitting || trimmed.length === 0}
                    className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 flex items-center gap-2"
                  >
                    {submitting ? (
                      <>
                        <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                        Sending...
                      </>
                    ) : (
                      "Send"
                    )}
                  </button>
                </>
              )}
            </div>
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
