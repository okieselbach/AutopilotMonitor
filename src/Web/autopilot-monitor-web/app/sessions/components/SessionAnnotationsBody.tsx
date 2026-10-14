"use client";

import {
  ANNOTATION_MAX_NOTE_LENGTH,
  ANNOTATION_VERDICTS,
  buildPutBody,
  hasContent,
  LANE_LABELS,
  VERDICT_DESCRIPTIONS,
  VERDICT_LABELS,
  VERDICT_PILL_CLASSES,
  type AnnotationVerdict,
} from "./sessionAnnotationLogic";
import type { SessionAnnotationsState } from "../hooks/useSessionAnnotations";

/**
 * The annotation lanes of a session (verdict + note per lane, editable where the server allows):
 * the body of the session page's annotations card and of the diagnosis page's team notes.
 */
export default function SessionAnnotationsBody({ state }: { state: SessionAnnotationsState }) {
  const { lanes, annotations, writableLanes, loaded, laneEdit, setLaneEdit, handleSave } = state;

  return (
    <div className="space-y-5">
      {lanes.map((lane) => {
        const annotation = annotations[lane];
        const writable = writableLanes.has(lane);
        const edit = laneEdit(lane);

        if (!writable && !hasContent(annotation)) {
          return null;
        }

        return (
          <div key={lane} className="border border-gray-200 rounded-lg p-4">
            <div className="flex items-center flex-wrap gap-2 mb-2">
              <span className="text-sm font-medium text-gray-900">{LANE_LABELS[lane]}</span>
              {lane === "globaladmin" && (
                <span
                  className="inline-flex px-2 py-0.5 rounded-full text-xs font-medium bg-violet-50 text-violet-700"
                  title="This lane is platform-internal: it is filtered out of every tenant response server-side."
                >
                  never visible to the tenant
                </span>
              )}
              {annotation?.verdict != null && !writable && (
                <span
                  title={VERDICT_DESCRIPTIONS[annotation.verdict as AnnotationVerdict]}
                  className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${VERDICT_PILL_CLASSES[annotation.verdict] ?? "bg-gray-100 text-gray-600"}`}
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
                    const selected = edit.verdict === verdict;
                    return (
                      <button
                        key={verdict}
                        type="button"
                        role="radio"
                        aria-checked={selected}
                        title={VERDICT_DESCRIPTIONS[verdict]}
                        onClick={() => setLaneEdit(lane, { verdict: selected ? null : verdict, saveResult: null })}
                        className={`px-3 py-1.5 rounded-full text-xs font-medium border transition-colors ${
                          selected
                            ? `${VERDICT_PILL_CLASSES[verdict]} border-transparent ring-1 ring-inset ring-gray-300`
                            : "bg-white text-gray-600 border-gray-300 hover:bg-gray-50"
                        }`}
                      >
                        {VERDICT_LABELS[verdict]}
                      </button>
                    );
                  })}
                </div>
                <textarea
                  value={edit.note}
                  onChange={(e) => setLaneEdit(lane, { note: e.target.value, saveResult: null })}
                  rows={3}
                  maxLength={ANNOTATION_MAX_NOTE_LENGTH}
                  placeholder="Optional note — what actually happened, what the analysis missed…"
                  className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500"
                />
                <div className="flex items-center justify-between mt-2">
                  <div className="text-sm min-h-[1.25rem]">
                    {edit.saveResult === "saved" ? (
                      <span className="inline-flex items-center gap-1 text-green-600">
                        <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                        </svg>
                        Saved
                      </span>
                    ) : edit.saveResult ? (
                      <span className="text-red-600">{edit.saveResult}</span>
                    ) : edit.note.length > 0 ? (
                      <span className="text-xs text-gray-400">
                        {edit.note.length}/{ANNOTATION_MAX_NOTE_LENGTH}
                      </span>
                    ) : null}
                  </div>
                  <button
                    onClick={() => handleSave(lane)}
                    disabled={edit.saving || (!hasContent(annotation) && buildPutBody(edit.verdict, edit.note).isClear)}
                    className="inline-flex items-center gap-2 px-4 py-1.5 bg-green-600 text-white text-sm font-medium rounded-md hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {edit.saving && (
                      <svg className="animate-spin w-4 h-4" fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8v4a4 4 0 00-4 4H4z" />
                      </svg>
                    )}
                    {edit.saving
                      ? "Saving…"
                      : hasContent(annotation) && buildPutBody(edit.verdict, edit.note).isClear
                        ? "Clear"
                        : "Save"}
                  </button>
                </div>
              </>
            ) : (
              annotation?.note && <p className="text-sm text-gray-700 whitespace-pre-wrap">{annotation.note}</p>
            )}
          </div>
        );
      })}

      {loaded && lanes.every((lane) => !writableLanes.has(lane) && !hasContent(annotations[lane])) && (
        <p className="text-sm text-gray-400">No annotations for this session.</p>
      )}
    </div>
  );
}
