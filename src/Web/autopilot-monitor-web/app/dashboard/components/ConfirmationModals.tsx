"use client";

import { useState } from "react";
import { BULK_DELETE_CONFIRM_WORD, requiresTypedConfirmation } from "../hooks/bulkActions";

/** Every target by name, scrollable — the admin must be able to see all of what goes away. */
function TargetList({ names }: { names: string[] }) {
  return (
    <ul className="mb-2 max-h-40 overflow-y-auto rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm text-gray-800">
      {names.map((name, i) => (
        <li key={`${name}-${i}`} className="truncate font-medium">{name}</li>
      ))}
    </ul>
  );
}

interface DeleteConfirmModalProps {
  targets: { sessionId: string; tenantId: string; deviceName?: string }[];
  onConfirm: () => void;
  onCancel: () => void;
}

export function DeleteConfirmModal({ targets, onConfirm, onCancel }: DeleteConfirmModalProps) {
  const single = targets.length === 1 ? targets[0] : null;
  // Larger batches need the confirmation word typed; the modal unmounts on close, so the
  // field starts empty every time it opens.
  const typed = requiresTypedConfirmation(targets.length);
  const [confirmText, setConfirmText] = useState("");
  const armed = !typed || confirmText === BULK_DELETE_CONFIRM_WORD;
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
            <h3 className="ml-4 text-lg font-semibold text-gray-900">
              {single ? "Delete Session" : `Delete ${targets.length} Sessions`}
            </h3>
          </div>

          <div className="mb-6">
            <p className="text-sm text-gray-700 mb-2">
              This action is <span className="font-semibold text-red-600">irreversible</span>!
            </p>
            {single ? (
              <p className="text-sm text-gray-700 mb-2">
                The session <span className="font-mono text-xs">{single.sessionId}</span> for device <span className="font-semibold">{single.deviceName || 'Unknown'}</span> and all associated events will be permanently deleted.
              </p>
            ) : (
              <>
                <p className="text-sm text-gray-700 mb-2">
                  These <span className="font-semibold">{targets.length} sessions</span> and all their associated events will be permanently deleted:
                </p>
                <TargetList names={targets.map((t) => t.deviceName || t.sessionId)} />
              </>
            )}
            <p className="text-sm text-gray-600">
              Do you want to continue?
            </p>
            {typed && (
              <label className="block mt-4 text-sm text-gray-700">
                Type <span className="font-mono font-bold">{BULK_DELETE_CONFIRM_WORD}</span> to confirm
                <input
                  type="text"
                  value={confirmText}
                  onChange={(e) => setConfirmText(e.target.value)}
                  autoFocus
                  autoComplete="off"
                  spellCheck={false}
                  className="mt-1 w-full px-3 py-2 border border-gray-300 rounded-md font-mono text-sm focus:outline-none focus:ring-2 focus:ring-red-500"
                />
              </label>
            )}
          </div>

          <div className="flex justify-end gap-3">
            <button
              onClick={onCancel}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={onConfirm}
              disabled={!armed}
              className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              {single ? "Delete" : `Delete ${targets.length}`}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

interface BlockConfirmModalProps {
  targets: { serialNumber: string; tenantId: string; deviceName?: string }[];
  blockingDevice: boolean;
  onConfirm: () => void;
  onCancel: () => void;
}

export function BlockConfirmModal({ targets, blockingDevice, onConfirm, onCancel }: BlockConfirmModalProps) {
  const single = targets.length === 1 ? targets[0] : null;
  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={onCancel}>
      <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
        <div className="p-6">
          <div className="flex items-center mb-4">
            <div className="flex-shrink-0 w-12 h-12 bg-orange-100 rounded-full flex items-center justify-center">
              <svg className="w-6 h-6 text-orange-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
              </svg>
            </div>
            <h3 className="ml-4 text-lg font-semibold text-gray-900">
              {single ? "Block Device" : `Block ${targets.length} Devices`}
            </h3>
          </div>

          <div className="mb-6">
            {single ? (
              <p className="text-sm text-gray-700 mb-2">
                Device <span className="font-semibold">{single.deviceName || single.serialNumber}</span> (Serial: <span className="font-mono text-xs">{single.serialNumber}</span>) will be blocked for <span className="font-semibold">24 hours</span>.
              </p>
            ) : (
              <>
                <p className="text-sm text-gray-700 mb-2">
                  These <span className="font-semibold">{targets.length} devices</span> will be blocked for <span className="font-semibold">24 hours</span>:
                </p>
                <TargetList names={targets.map((t) => t.deviceName || t.serialNumber)} />
              </>
            )}
            <p className="text-sm text-gray-600">
              The agent will stop uploading data while blocked. Do you want to continue?
            </p>
          </div>

          <div className="flex justify-end gap-3">
            <button
              onClick={onCancel}
              className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={onConfirm}
              disabled={blockingDevice}
              className="px-4 py-2 bg-orange-600 text-white rounded-md hover:bg-orange-700 transition-colors disabled:opacity-50"
            >
              {blockingDevice ? 'Blocking...' : single ? 'Block' : `Block ${targets.length}`}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
