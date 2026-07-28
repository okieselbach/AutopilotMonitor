"use client";

import { Session } from "@/types";

interface MarkFailedModalProps {
  show: boolean;
  session: Session | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export default function MarkFailedModal({ show, session, onConfirm, onCancel }: MarkFailedModalProps) {
  if (!show || !session) return null;

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onCancel}>
      <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
        <div className="p-6">
          <div className="flex items-center mb-4">
            <div className="flex-shrink-0 w-12 h-12 bg-red-100 rounded-full flex items-center justify-center">
              <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
            </div>
            <h3 className="ml-4 text-lg font-semibold text-gray-900">Mark Session as Failed</h3>
          </div>
          <div className="mb-6">
            <p className="text-sm text-gray-700 mb-2">
              You are about to manually mark this session as <span className="font-semibold text-red-600">Failed</span>.
            </p>
            <p className="text-sm text-gray-700 mb-2">
              Session <span className="font-mono text-xs">{session.sessionId}</span> for device <span className="font-semibold">{session.deviceName || session.serialNumber}</span> will be marked as failed with the reason "Manually marked as failed by administrator".
            </p>
            <p className="text-sm text-gray-600">
              This action will update the session status and cannot be undone. Do you want to continue?
            </p>
          </div>
          <div className="flex justify-end gap-3">
            <button onClick={onCancel} className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors">
              Cancel
            </button>
            <button onClick={onConfirm} className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors">
              Mark as Failed
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
