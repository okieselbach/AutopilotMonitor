"use client";

import { Session } from "@/types";

interface AdminOverrideModalProps {
  show: boolean;
  action: "failed" | "succeeded";
  session: Session | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export default function AdminOverrideModal({ show, action, session, onConfirm, onCancel }: AdminOverrideModalProps) {
  if (!show || !session) return null;

  const isFailed = action === "failed";
  const colorClass = isFailed ? "red" : "green";
  const label = isFailed ? "Failed" : "Succeeded";

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onCancel}>
      <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
        <div className="p-6">
          <div className="flex items-center mb-4">
            <div className={`flex-shrink-0 w-12 h-12 bg-${colorClass}-100 rounded-full flex items-center justify-center`}>
              {isFailed ? (
                <svg className={`w-6 h-6 text-${colorClass}-600`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                </svg>
              ) : (
                <svg className={`w-6 h-6 text-${colorClass}-600`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              )}
            </div>
            <h3 className="ml-4 text-lg font-semibold text-gray-900">Mark Session as {label}</h3>
          </div>
          <div className="mb-6">
            <p className="text-sm text-gray-700 mb-2">
              You are about to manually mark this session as <span className={`font-semibold text-${colorClass}-600`}>{label}</span>.
            </p>
            <p className="text-sm text-gray-700 mb-2">
              Session <span className="font-mono text-xs">{session.sessionId}</span> for device <span className="font-semibold">{session.deviceName || session.serialNumber}</span> will be marked as {action} with the reason &quot;Manually marked as {action} by administrator&quot;.
            </p>
            <div className="bg-amber-50 border border-amber-200 rounded-md p-3 mt-3">
              <p className="text-sm text-amber-800 font-medium mb-1">Agent Cleanup Warning</p>
              <p className="text-xs text-amber-700">
                This action will trigger the agent to stop monitoring and perform cleanup on the device (final data upload, diagnostics collection, and potentially self-destruct based on tenant configuration). If the agent is currently idle, the cleanup will occur on the next device restart.
              </p>
            </div>
            <p className="text-sm text-gray-600 mt-3">
              This action cannot be undone. Do you want to continue?
            </p>
          </div>
          <div className="flex justify-end gap-3">
            <button onClick={onCancel} className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors">
              Cancel
            </button>
            <button
              onClick={onConfirm}
              className={`px-4 py-2 ${isFailed ? 'bg-red-600 hover:bg-red-700' : 'bg-green-600 hover:bg-green-700'} text-white rounded-md transition-colors`}
            >
              Mark as {label}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
