"use client";

import { useState } from "react";
import { LANE_LABELS, VERDICT_LABELS, VERDICT_PILL_CLASSES, type AnnotationUser, type AnnotationVerdict } from "./sessionAnnotationLogic";
import { useSessionAnnotations } from "../hooks/useSessionAnnotations";
import SessionAnnotationsBody from "./SessionAnnotationsBody";

/**
 * Session annotations: per-lane human verdict + note about this enrollment's analysis
 * (Operator / Tenant Admin / platform team). The structured verdicts feed rule-quality
 * evaluation (confirmed vs false-positive per rule), so lanes are written by their own
 * role only. Which lanes are writable comes from the SERVER (`writableLanes` on the
 * GET — computed by the same matrix the PUT re-gates with), so UI and enforcement
 * cannot drift. Read is fail-soft; an error shows the empty state rather than
 * breaking the page.
 */
export default function SessionAnnotationsCard({
  sessionId,
  effectiveTenantId,
  user,
  getAccessToken,
}: {
  sessionId: string;
  /** Tenant used for the API calls (resolved session tenant / GA override); undefined = own tenant. */
  effectiveTenantId?: string;
  user: AnnotationUser | null | undefined;
  getAccessToken: () => Promise<string | null>;
}) {
  const state = useSessionAnnotations({ sessionId, effectiveTenantId, user, getAccessToken });
  const { annotations, annotatedLanes } = state;
  // Collapsed by default: the verdict comes at the END of a diagnosis — the card must
  // not take space away from the details/analysis work above it. The header summary
  // (count + verdict pills) still shows at a glance whether a verdict exists.
  const [expanded, setExpanded] = useState(false);

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <div
        onClick={() => setExpanded(!expanded)}
        className="flex items-start justify-between gap-2 w-full text-left cursor-pointer"
      >
        <div className="flex items-center flex-wrap gap-x-2 gap-y-1 min-w-0">
          <svg className="w-6 h-6 shrink-0 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M16.862 4.487l1.687-1.688a1.875 1.875 0 112.652 2.652L10.582 16.07a4.5 4.5 0 01-1.897 1.13L6 18l.8-2.685a4.5 4.5 0 011.13-1.897l8.932-8.931zm0 0L19.5 7.125" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900">Annotations</h2>
          {annotatedLanes.length > 0 && (
            <>
              <span className="text-xs text-gray-400">
                ({annotatedLanes.length} {annotatedLanes.length === 1 ? "annotation" : "annotations"})
              </span>
              <div className="flex items-center flex-wrap gap-2 text-xs">
                {annotatedLanes.map((lane) => {
                  const verdict = annotations[lane]?.verdict;
                  return verdict != null ? (
                    <span
                      key={lane}
                      className={`px-2 py-0.5 rounded-full font-medium ${VERDICT_PILL_CLASSES[verdict] ?? "bg-gray-100 text-gray-600"}`}
                      title={LANE_LABELS[lane]}
                    >
                      {VERDICT_LABELS[verdict as AnnotationVerdict] ?? verdict}
                    </span>
                  ) : null;
                })}
              </div>
            </>
          )}
        </div>
        <svg className={`w-5 h-5 shrink-0 text-gray-400 transition-transform duration-200 ${expanded ? "rotate-90" : ""}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </div>

      {expanded && (
        <>
          <p className="text-sm text-gray-500 mt-2 mb-4">
            Human assessment of this enrollment&apos;s analysis — confirmed root causes and
            corrections feed rule quality.
          </p>
          <SessionAnnotationsBody state={state} />
        </>
      )}
    </div>
  );
}
