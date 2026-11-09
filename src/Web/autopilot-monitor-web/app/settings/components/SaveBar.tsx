"use client";

import { useState, useCallback, useRef } from "react";
import { apiErrorText } from "@/lib/apiClient";

interface SaveBarProps {
  /** A failed save throws: the bar shows the reason next to the button, where the user is looking. */
  onSave: () => Promise<void> | void;
  saving: boolean;
  /** When false, Save is disabled (read-only viewer). Defaults to true. */
  canSave?: boolean;
  /** Input the user can still fix: Save is disabled with this reason. */
  saveBlockedReason?: string;
  /**
   * Set by a card that tracks its edits. While true the bar says so and sticks to the bottom of the
   * viewport as long as the card reaches below it, so Save stays in reach; while false Save is
   * disabled, there is nothing to save. Left out (no tracking), Save stays available.
   */
  unsaved?: boolean;
}

export default function SaveBar({ onSave, saving, canSave = true, saveBlockedReason, unsaved }: SaveBarProps) {
  const [saveResult, setSaveResult] = useState<"idle" | "saving" | "saved" | "error">("idle");
  const [failureReason, setFailureReason] = useState("");
  const savedTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const handleSave = useCallback(async () => {
    clearTimeout(savedTimer.current);
    setSaveResult("saving");
    try {
      await onSave();
      setSaveResult("saved");
      savedTimer.current = setTimeout(() => setSaveResult("idle"), 3000);
    } catch (err) {
      // Stays until the next Save: unlike "Saved!" it needs reading and acting on.
      setFailureReason(apiErrorText(err, ""));
      setSaveResult("error");
    }
  }, [onSave]);

  // The bar stays pinned through the save and its "Saved!": the edits count as saved the moment the
  // save lands, and unpinning then would carry the confirmation out of view with the bar.
  const pinned = unsaved === true || saveResult === "saving" || saveResult === "saved";
  // A tracking card with nothing unsaved: Save is greyed out, so an enabled Save always means "to do".
  const nothingToSave = unsaved === false;

  return (
    <div
      data-save-bar-pinned={pinned || undefined}
      className={`flex flex-wrap items-center justify-end gap-3 pt-4 border-t border-gray-200 ${
        pinned ? "sticky bottom-0 z-10 pb-4 bg-white" : ""
      }`}
      // The pinned bar keeps 1rem below Save at the viewport edge; the same 1rem comes off its margin so
      // the card does not grow. Inline, because the card's space-y-* resets this child's margin-bottom.
      style={pinned ? { marginBottom: "-1rem" } : undefined}
    >
      {unsaved && (
        <span className="mr-auto flex items-center gap-2 text-sm font-medium text-amber-700">
          <span className="h-2 w-2 shrink-0 rounded-full bg-amber-500" aria-hidden="true" />
          Unsaved changes
        </span>
      )}
      {!canSave && (
        <span className="text-sm text-gray-400">Read-only</span>
      )}
      {saveBlockedReason && (
        <span className="min-w-0 break-words text-sm text-amber-600">{saveBlockedReason}</span>
      )}
      {/* Inline feedback (errors only — success is shown via button state) */}
      {saveResult === "error" && (
        <span role="alert" className="flex items-center min-w-0 text-sm text-red-600 font-medium">
          <svg className="w-4 h-4 mr-1 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M6 18L18 6M6 6l12 12" />
          </svg>
          <span className="min-w-0 break-words">Save failed{failureReason && `: ${failureReason}`}</span>
        </span>
      )}
      <button
        onClick={handleSave}
        disabled={saving || !canSave || !!saveBlockedReason || nothingToSave}
        title={nothingToSave && saveResult !== "saved" ? "No changes to save" : undefined}
        className={`px-4 py-2 rounded-md text-sm text-white font-medium disabled:opacity-50 disabled:cursor-not-allowed transition-all flex items-center space-x-2 ${
          // "Saved!" keeps full color although the card has nothing left to save.
          saveResult === "saved" ? "bg-emerald-600 disabled:opacity-100" : "bg-green-600 enabled:hover:bg-green-700"
        }`}
      >
        {saving ? (
          <>
            <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
            <span>Saving...</span>
          </>
        ) : saveResult === "saved" ? (
          <>
            <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
            </svg>
            <span>Saved!</span>
          </>
        ) : (
          <span>Save</span>
        )}
      </button>
    </div>
  );
}
