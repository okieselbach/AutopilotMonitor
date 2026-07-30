"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch } from "@/lib/authenticatedFetch";

type Phase = "loading" | "bubble" | "form" | "thankyou" | "hidden";

// Aligned with the offboarding-feedback textarea — gives users room for substantive
// comments without hitting an artificial 500-char limit. Backend cap matches.
const FEEDBACK_MAX_CHARS = 4096;

export default function FeedbackBubble() {
  const { isAuthenticated, user, getAccessToken } = useAuth();
  const [phase, setPhase] = useState<Phase>("loading");
  const [rating, setRating] = useState<number>(0);
  const [hoveredStar, setHoveredStar] = useState<number>(0);
  const [comment, setComment] = useState("");
  const [submitting, setSubmitting] = useState(false);

  // Check eligibility on mount
  useEffect(() => {
    if (!isAuthenticated || !user) return;

    let cancelled = false;

    const checkEligibility = async () => {
      try {
        const response = await authenticatedFetch(
          api.feedback.status(),
          getAccessToken
        );
        if (cancelled) return;

        if (response.ok) {
          const data = await response.json();
          if (data.eligible) {
            // Show bubble after 3s delay
            setTimeout(() => {
              if (!cancelled) setPhase("bubble");
            }, 3000);
          } else {
            setPhase("hidden");
          }
        } else {
          setPhase("hidden");
        }
      } catch {
        setPhase("hidden");
      }
    };

    checkEligibility();
    return () => { cancelled = true; };
  }, [isAuthenticated, user, getAccessToken]);

  const handleDismiss = useCallback(async () => {
    setPhase("hidden");
    try {
      await authenticatedFetch(api.feedback.submit(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ dismissed: true }),
      });
    } catch {
      // Best effort
    }
  }, [getAccessToken]);

  const handleSubmit = useCallback(async () => {
    if (rating === 0 || submitting) return;
    setSubmitting(true);

    try {
      await authenticatedFetch(api.feedback.submit(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ rating, comment: comment.trim() || null, dismissed: false }),
      });

      setPhase("thankyou");
      setTimeout(() => setPhase("hidden"), 2000);
    } catch {
      // Still show thank you — feedback may have been recorded
      setPhase("thankyou");
      setTimeout(() => setPhase("hidden"), 2000);
    } finally {
      setSubmitting(false);
    }
  }, [rating, comment, submitting, getAccessToken]);

  // Don't render anything while loading or hidden
  if (phase === "loading" || phase === "hidden") return null;

  return (
    <div className="fixed bottom-20 right-6 z-40 sm:bottom-20 sm:right-6">
      {/* Collapsed Bubble */}
      {phase === "bubble" && (
        <div
          className="flex items-center gap-2 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-full px-4 py-2.5 shadow-lg cursor-pointer hover:shadow-xl transition-all duration-300 animate-fade-in-up"
          onClick={() => setPhase("form")}
        >
          <svg className="w-4 h-4 text-yellow-400 flex-shrink-0" fill="currentColor" viewBox="0 0 20 20">
            <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
          </svg>
          <span className="text-sm font-medium text-gray-700 dark:text-gray-200">Feedback</span>
          <button
            onClick={(e) => { e.stopPropagation(); handleDismiss(); }}
            className="ml-1 text-gray-400 hover:text-gray-600 dark:text-gray-500 dark:hover:text-gray-300 transition-colors"
            aria-label="Dismiss feedback"
          >
            <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
      )}

      {/* Expanded Form */}
      {phase === "form" && (
        <div className="w-80 bg-white dark:bg-gray-800 border border-gray-200 dark:border-gray-700 rounded-xl shadow-xl animate-fade-in-up">
          {/* Header */}
          <div className="flex items-center justify-between px-4 pt-4 pb-2">
            <h3 className="text-sm font-semibold text-gray-900 dark:text-gray-100">
              How&apos;s your experience?
            </h3>
            <button
              onClick={handleDismiss}
              className="text-gray-400 hover:text-gray-600 dark:text-gray-500 dark:hover:text-gray-300 transition-colors"
              aria-label="Dismiss feedback"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          <div className="px-4 pb-4 space-y-3">
            {/* Star Rating */}
            <div className="flex gap-1">
              {[1, 2, 3, 4, 5].map((star) => (
                <button
                  key={star}
                  onClick={() => setRating(star)}
                  onMouseEnter={() => setHoveredStar(star)}
                  onMouseLeave={() => setHoveredStar(0)}
                  className="min-h-[44px] min-w-[44px] flex items-center justify-center transition-transform hover:scale-110"
                  aria-label={`Rate ${star} star${star > 1 ? "s" : ""}`}
                >
                  <svg
                    className={`w-7 h-7 transition-colors ${
                      star <= (hoveredStar || rating)
                        ? "text-yellow-400"
                        : "text-gray-300 dark:text-gray-600"
                    }`}
                    fill="currentColor"
                    viewBox="0 0 20 20"
                  >
                    <path d="M9.049 2.927c.3-.921 1.603-.921 1.902 0l1.07 3.292a1 1 0 00.95.69h3.462c.969 0 1.371 1.24.588 1.81l-2.8 2.034a1 1 0 00-.364 1.118l1.07 3.292c.3.921-.755 1.688-1.54 1.118l-2.8-2.034a1 1 0 00-1.175 0l-2.8 2.034c-.784.57-1.838-.197-1.539-1.118l1.07-3.292a1 1 0 00-.364-1.118L2.98 8.72c-.783-.57-.38-1.81.588-1.81h3.461a1 1 0 00.951-.69l1.07-3.292z" />
                  </svg>
                </button>
              ))}
            </div>

            {/* Comment */}
            <div>
              <textarea
                value={comment}
                onChange={(e) => setComment(e.target.value.slice(0, FEEDBACK_MAX_CHARS))}
                placeholder="Any feedback? (optional)"
                rows={2}
                className="w-full px-3 py-2 text-sm bg-gray-50 dark:bg-gray-700 text-gray-900 dark:text-gray-100 border border-gray-300 dark:border-gray-600 rounded-lg resize-none focus:ring-2 focus:ring-green-500 focus:border-green-500 outline-none placeholder-gray-400 dark:placeholder-gray-500"
              />
              {comment.length > 0 && (
                <p className="text-xs text-gray-400 text-right mt-0.5">{comment.length}/{FEEDBACK_MAX_CHARS}</p>
              )}
            </div>

            {/* Submit Button */}
            <button
              onClick={handleSubmit}
              disabled={rating === 0 || submitting}
              className="w-full px-4 py-2 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              {submitting ? "Sending..." : "Send Feedback"}
            </button>
          </div>
        </div>
      )}

      {/* Thank You */}
      {phase === "thankyou" && (
        <div className="flex items-center gap-2 bg-white dark:bg-gray-800 border border-green-200 dark:border-green-700 rounded-full px-4 py-2.5 shadow-lg animate-fade-in-up">
          <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
          </svg>
          <span className="text-sm font-medium text-gray-700 dark:text-gray-200">
            Thanks for your feedback!
          </span>
        </div>
      )}
    </div>
  );
}
