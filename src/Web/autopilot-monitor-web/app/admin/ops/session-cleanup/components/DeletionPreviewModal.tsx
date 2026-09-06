"use client";

import { DeletionManifestSummaryView } from "./DeletionManifestSummaryView";
import { ModalPortal } from "@/components/ModalPortal";

interface DeletionPreviewModalProps {
  tenantId: string;
  sessionId: string;
  manifestId: string;
  onClose: () => void;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}

/**
 * Modal wrapper around {@link DeletionManifestSummaryView}. Invoked from the In-Flight /
 * Poisoned / Stranded tabs when an operator clicks "Preview" on a row. Restore Browser tab
 * embeds the same summary view inline in its right-hand panel — that's why the body is its
 * own reusable component.
 */
export function DeletionPreviewModal({
  tenantId,
  sessionId,
  manifestId,
  onClose,
  getAccessToken,
}: DeletionPreviewModalProps) {
  return (
    <ModalPortal>
      <div
        className="fixed inset-0 z-50 flex items-center justify-center bg-black bg-opacity-50 p-4"
        onClick={onClose}
      >
        <div
          className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-3xl w-full max-h-[80vh] overflow-y-auto"
          onClick={(e) => e.stopPropagation()}
        >
          <div className="sticky top-0 bg-white dark:bg-gray-800 border-b border-gray-200 dark:border-gray-700 px-6 py-4 flex items-start justify-between rounded-t-lg">
            <div>
              <h2 className="text-lg font-semibold text-gray-900 dark:text-white">Stored Manifest</h2>
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-1 font-mono">
                {tenantId} / {sessionId}
              </p>
              <p className="text-xs text-gray-500 dark:text-gray-400 mt-0.5">
                Reads the persisted snapshot the worker captured at cascade start —
                <strong className="ml-1">not</strong> a fresh dry-run.
              </p>
            </div>
            <button
              onClick={onClose}
              className="text-gray-400 hover:text-gray-600 dark:hover:text-gray-200"
              aria-label="Close"
            >
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>

          <div className="px-6 py-4">
            <DeletionManifestSummaryView
              tenantId={tenantId}
              sessionId={sessionId}
              manifestId={manifestId}
              getAccessToken={getAccessToken}
              showDownload
            />
          </div>

          <div className="sticky bottom-0 bg-gray-50 dark:bg-gray-900 px-6 py-3 border-t border-gray-200 dark:border-gray-700 flex justify-end gap-2 rounded-b-lg">
            <button
              onClick={onClose}
              className="px-3 py-2 text-sm bg-gray-200 dark:bg-gray-700 text-gray-800 dark:text-gray-100 rounded hover:bg-gray-300 dark:hover:bg-gray-600"
            >
              Close
            </button>
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
