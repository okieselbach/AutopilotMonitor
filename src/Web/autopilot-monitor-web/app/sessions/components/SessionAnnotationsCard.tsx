"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import {
  ANNOTATION_MAX_NOTE_LENGTH,
  ANNOTATION_VERDICTS,
  buildPutBody,
  canWriteLane,
  hasContent,
  LANE_LABELS,
  VERDICT_DESCRIPTIONS,
  VERDICT_LABELS,
  visibleLanes,
  validateNote,
  type AnnotationLane,
  type AnnotationUser,
  type AnnotationVerdict,
  type SessionAnnotationDto,
} from "./sessionAnnotationLogic";

/**
 * Session annotations: per-lane human verdict + note about this enrollment's analysis
 * (Operator / Tenant Admin / platform team). The structured verdicts feed rule-quality
 * evaluation (confirmed vs false-positive per rule), so lanes are written by their own
 * role only — the backend re-gates every save. Read is fail-soft; an error shows the
 * empty state rather than breaking the page.
 */

interface AnnotationsResponse {
  success: boolean;
  annotations?: SessionAnnotationDto[] | null;
}

const VERDICT_PILL: Record<string, string> = {
  root_cause_confirmed: "bg-green-100 text-green-800",
  analysis_wrong: "bg-red-100 text-red-800",
  different_problem: "bg-amber-100 text-amber-800",
  inconclusive: "bg-slate-100 text-slate-600",
};

interface LaneEditState {
  verdict: string | null;
  note: string;
  saving: boolean;
  /** "saved" | error message | null. Typing clears it. */
  saveResult: string | null;
}

export default function SessionAnnotationsCard({
  sessionId,
  effectiveTenantId,
  user,
  isCrossTenantView,
  getAccessToken,
}: {
  sessionId: string;
  /** Tenant used for the API calls (resolved session tenant / GA override); undefined = own tenant. */
  effectiveTenantId?: string;
  user: AnnotationUser | null | undefined;
  /** True when the session belongs to a different tenant than the caller's own. */
  isCrossTenantView: boolean;
  getAccessToken: () => Promise<string | null>;
}) {
  const [annotations, setAnnotations] = useState<Partial<Record<string, SessionAnnotationDto>>>({});
  const [loaded, setLoaded] = useState(false);
  const [edit, setEdit] = useState<Partial<Record<string, LaneEditState>>>({});
  // Collapsed by default: the verdict comes at the END of a diagnosis — the card must
  // not take space away from the details/analysis work above it. The header summary
  // (count + verdict pills) still shows at a glance whether a verdict exists.
  const [expanded, setExpanded] = useState(false);

  const lanes = visibleLanes(user);

  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    (async () => {
      try {
        const response = await authenticatedFetch(
          api.sessions.annotations(sessionId, effectiveTenantId),
          getAccessToken
        );
        if (!response.ok) return;
        const json = (await response.json()) as AnnotationsResponse;
        if (cancelled) return;
        const byLane: Partial<Record<string, SessionAnnotationDto>> = {};
        for (const a of json.annotations ?? []) byLane[a.lane] = a;
        setAnnotations(byLane);
        const seeded: Partial<Record<string, LaneEditState>> = {};
        for (const [lane, a] of Object.entries(byLane)) {
          seeded[lane] = {
            verdict: a?.verdict ?? null,
            note: a?.note ?? "",
            saving: false,
            saveResult: null,
          };
        }
        setEdit(seeded);
      } catch {
        // fail-soft: card shows the empty state
      } finally {
        if (!cancelled) setLoaded(true);
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, effectiveTenantId]);

  const laneEdit = (lane: AnnotationLane): LaneEditState =>
    edit[lane] ?? { verdict: null, note: "", saving: false, saveResult: null };

  const setLaneEdit = useCallback((lane: AnnotationLane, patch: Partial<LaneEditState>) => {
    setEdit((prev) => ({
      ...prev,
      [lane]: { ...(prev[lane] ?? { verdict: null, note: "", saving: false, saveResult: null }), ...patch },
    }));
  }, []);

  const handleSave = async (lane: AnnotationLane) => {
    const state = laneEdit(lane);
    const noteError = validateNote(state.note);
    if (noteError) {
      setLaneEdit(lane, { saveResult: noteError });
      return;
    }
    const { body, isClear } = buildPutBody(state.verdict, state.note);
    setLaneEdit(lane, { saving: true, saveResult: null });
    try {
      const res = await authenticatedFetch(
        api.sessions.annotation(sessionId, lane, effectiveTenantId),
        getAccessToken,
        {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(body),
        }
      );
      if (!res.ok) throw new Error(`Failed to save annotation: ${res.statusText}`);
      const json = (await res.json()) as { success: boolean; annotation?: SessionAnnotationDto };
      setAnnotations((prev) => {
        const next = { ...prev };
        if (isClear) delete next[lane];
        else if (json.annotation) next[lane] = json.annotation;
        return next;
      });
      setLaneEdit(lane, { saving: false, saveResult: "saved" });
    } catch (err) {
      if (err instanceof TokenExpiredError) console.error("Session expired while saving annotation");
      setLaneEdit(lane, {
        saving: false,
        saveResult: err instanceof Error ? err.message : "Failed to save annotation",
      });
    }
  };

  const annotatedLanes = lanes.filter((lane) => hasContent(annotations[lane]));

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
                      className={`px-2 py-0.5 rounded-full font-medium ${VERDICT_PILL[verdict] ?? "bg-gray-100 text-gray-600"}`}
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

      <div className="space-y-5">
        {lanes.map((lane) => {
          const annotation = annotations[lane];
          const writable = canWriteLane(lane, user, isCrossTenantView);
          const state = laneEdit(lane);

          if (!writable && !hasContent(annotation)) {
            return null;
          }

          return (
            <div key={lane} className="border border-gray-200 rounded-lg p-4">
              <div className="flex items-center flex-wrap gap-2 mb-2">
                <span className="text-sm font-medium text-gray-900">{LANE_LABELS[lane]}</span>
                {annotation?.verdict != null && !writable && (
                  <span
                    title={VERDICT_DESCRIPTIONS[annotation.verdict as AnnotationVerdict]}
                    className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${VERDICT_PILL[annotation.verdict] ?? "bg-gray-100 text-gray-600"}`}
                  >
                    {VERDICT_LABELS[annotation.verdict as AnnotationVerdict] ?? annotation.verdict}
                  </span>
                )}
                {annotation?.authorDisplayName && (
                  <span className="text-xs text-gray-400">
                    {annotation.authorDisplayName}
                    {annotation.updatedAtUtc ? ` · ${new Date(annotation.updatedAtUtc).toLocaleString()}` : ""}
                  </span>
                )}
              </div>

              {writable ? (
                <>
                  <div className="flex flex-wrap gap-2 mb-3" role="radiogroup" aria-label={`${LANE_LABELS[lane]} verdict`}>
                    {ANNOTATION_VERDICTS.map((verdict) => {
                      const selected = state.verdict === verdict;
                      return (
                        <button
                          key={verdict}
                          type="button"
                          role="radio"
                          aria-checked={selected}
                          title={VERDICT_DESCRIPTIONS[verdict]}
                          onClick={() =>
                            setLaneEdit(lane, { verdict: selected ? null : verdict, saveResult: null })
                          }
                          className={`px-3 py-1.5 rounded-full text-xs font-medium border transition-colors ${
                            selected
                              ? `${VERDICT_PILL[verdict]} border-transparent ring-1 ring-inset ring-gray-300`
                              : "bg-white text-gray-600 border-gray-300 hover:bg-gray-50"
                          }`}
                        >
                          {VERDICT_LABELS[verdict]}
                        </button>
                      );
                    })}
                  </div>
                  <textarea
                    value={state.note}
                    onChange={(e) => setLaneEdit(lane, { note: e.target.value, saveResult: null })}
                    rows={3}
                    maxLength={ANNOTATION_MAX_NOTE_LENGTH}
                    placeholder="Optional note — what actually happened, what the analysis missed…"
                    className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                  />
                  <div className="flex items-center justify-between mt-2">
                    <div className="text-sm min-h-[1.25rem]">
                      {state.saveResult === "saved" ? (
                        <span className="inline-flex items-center gap-1 text-green-600">
                          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                          </svg>
                          Saved
                        </span>
                      ) : state.saveResult ? (
                        <span className="text-red-600">{state.saveResult}</span>
                      ) : state.note.length > 0 ? (
                        <span className="text-xs text-gray-400">
                          {state.note.length}/{ANNOTATION_MAX_NOTE_LENGTH}
                        </span>
                      ) : null}
                    </div>
                    <button
                      onClick={() => handleSave(lane)}
                      disabled={state.saving || (!hasContent(annotation) && buildPutBody(state.verdict, state.note).isClear)}
                      className="inline-flex items-center gap-2 px-4 py-1.5 bg-green-600 text-white text-sm font-medium rounded-md hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      {state.saving && (
                        <svg className="animate-spin w-4 h-4" fill="none" viewBox="0 0 24 24">
                          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                          <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
                        </svg>
                      )}
                      {state.saving
                        ? "Saving…"
                        : hasContent(annotation) && buildPutBody(state.verdict, state.note).isClear
                          ? "Clear"
                          : "Save"}
                    </button>
                  </div>
                </>
              ) : (
                annotation?.note && (
                  <p className="text-sm text-gray-700 whitespace-pre-wrap">{annotation.note}</p>
                )
              )}
            </div>
          );
        })}

        {loaded &&
          lanes.every(
            (lane) => !canWriteLane(lane, user, isCrossTenantView) && !hasContent(annotations[lane])
          ) && (
            <p className="text-sm text-gray-400">No annotations for this session.</p>
          )}
      </div>
      </>
      )}
    </div>
  );
}
