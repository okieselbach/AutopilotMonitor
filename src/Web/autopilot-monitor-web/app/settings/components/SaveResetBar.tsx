"use client";

import { useState, useCallback, useRef } from "react";
import { apiErrorText } from "@/lib/apiClient";

interface SaveResetBarProps {
  /** A failed save throws: the bar shows the reason next to the button, where the user is looking. */
  onSave: () => Promise<void> | void;
  onReset: () => void;
  saving: boolean;
  /** When false, Save/Reset are disabled (read-only viewer). Defaults to true. */
  canSave?: boolean;
  /** Input the user can still fix: Save is disabled with this reason, Reset stays available. */
  saveBlockedReason?: string;
}

export default function SaveResetBar({ onSave, onReset, saving, canSave = true, saveBlockedReason }: SaveResetBarProps) {
  const [saveResult, setSaveResult] = useState<"idle" | "saved" | "error">("idle");
  const [failureReason, setFailureReason] = useState("");
  const savedTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  const handleSave = useCallback(async () => {
    clearTimeout(savedTimer.current);
    setSaveResult("idle");
    try {
      await onSave();
      setSaveResult("saved");
      savedTimer.current = setTimeout(() => setSaveResult("idle"), 3000);
    } catch (err) {
      // Stays until the next Save or Reset: unlike "Saved!" it needs reading and acting on.
      setFailureReason(apiErrorText(err, ""));
      setSaveResult("error");
    }
  }, [onSave]);

  const handleReset = useCallback(() => {
    clearTimeout(savedTimer.current);
    setSaveResult("idle");
    onReset();
  }, [onReset]);

  return (
    <div className="flex flex-wrap items-center justify-end gap-3 pt-4 border-t border-gray-200">
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
        onClick={handleReset}
        disabled={saving || !canSave}
        className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm"
      >
        Reset
      </button>
      <button
        onClick={handleSave}
        disabled={saving || !canSave || !!saveBlockedReason}
        className={`px-4 py-2 rounded-md text-sm text-white font-medium disabled:opacity-50 disabled:cursor-not-allowed transition-all flex items-center space-x-2 ${
          saveResult === "saved" ? "bg-emerald-600" : "bg-green-600 hover:bg-green-700"
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
