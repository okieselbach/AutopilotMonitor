"use client";

import { useEffect, useState } from "react";

interface DeleteConfirmModalProps {
  open: boolean;
  title: string;
  description: React.ReactNode;
  busy?: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}

const CONFIRM_WORD = "DELETE";

/**
 * Lightweight typed-confirmation modal for destructive customs-archive actions.
 * Replaces the native window.confirm() so the dialog can render rich context
 * (tenant id, table/row, count) and require a typed "DELETE" before the
 * primary button activates.
 */
export function DeleteConfirmModal({
  open,
  title,
  description,
  busy = false,
  onCancel,
  onConfirm,
}: DeleteConfirmModalProps) {
  const [text, setText] = useState("");

  useEffect(() => {
    if (!open) setText("");
  }, [open]);

  if (!open) return null;

  const canConfirm = text === CONFIRM_WORD && !busy;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
      <div className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-md w-full p-6">
        <h3 className="text-lg font-bold text-gray-900 dark:text-white mb-2">{title}</h3>
        <div className="text-sm text-gray-700 dark:text-gray-300 mb-4">{description}</div>

        <div className="mb-4">
          <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
            Type <span className="font-bold text-red-600 dark:text-red-400">{CONFIRM_WORD}</span> to confirm
          </label>
          <input
            type="text"
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder={CONFIRM_WORD}
            disabled={busy}
            autoFocus
            autoComplete="off"
            className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-900 text-gray-900 dark:text-white focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
          />
        </div>

        <div className="flex space-x-3">
          <button
            type="button"
            onClick={onCancel}
            disabled={busy}
            className="flex-1 px-4 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-gray-700 dark:text-gray-200 bg-white dark:bg-gray-700 hover:bg-gray-50 dark:hover:bg-gray-600 disabled:opacity-50 transition-colors"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={onConfirm}
            disabled={!canConfirm}
            className="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {busy ? "Deleting…" : "Delete"}
          </button>
        </div>
      </div>
    </div>
  );
}
