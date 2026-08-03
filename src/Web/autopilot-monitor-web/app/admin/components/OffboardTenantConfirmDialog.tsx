"use client";

import { useState } from "react";

interface OffboardTenantConfirmDialogProps {
  tenantLabel: string;
  tenantId: string;
  saving: boolean;
  /** Failure of the offboard DELETE — rendered inside the dialog; a page-level
   *  banner would sit behind this overlay (z-[60]) and never be seen. */
  error: string | null;
  onCancel: () => void;
  onConfirm: () => void;
}

/**
 * GA confirmation for the irreversible tenant-offboarding cascade. Renders above the tenant
 * editor modal (z-[60] vs its z-50). Type-to-confirm ("OFFBOARD") mirrors the tenant-facing
 * self-service dialog — a plain OK button is not enough of a guard for a destructive action,
 * Global Admin included.
 */
export function OffboardTenantConfirmDialog({
  tenantLabel,
  tenantId,
  saving,
  error,
  onCancel,
  onConfirm,
}: OffboardTenantConfirmDialogProps) {
  const [confirmText, setConfirmText] = useState("");
  const armed = confirmText === "OFFBOARD";

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-[60] p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full">
        <div className="p-5 rounded-t-lg text-white bg-red-600">
          <h2 className="text-lg font-bold">Offboard tenant</h2>
          <p className="text-sm opacity-90 mt-0.5">{tenantLabel}</p>
          <p className="text-xs opacity-75">{tenantId}</p>
        </div>

        <div className="p-5 space-y-3 text-sm text-gray-700">
          <p>
            This queues the offboarding cascade: the tenant is <strong>suspended immediately</strong>,
            and after a short drain window <strong>all of its data is permanently deleted</strong> —
            sessions, events, rules, admins, and the tenant configuration itself.
          </p>
          <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-red-800">
            <strong>This cannot be undone,</strong> and it is <strong>not a ban</strong>: because the
            configuration row (including any suspension) is deleted, a new sign-in from this tenant
            re-onboards it from scratch — and re-activates it automatically while tenant
            auto-activation is enabled. To keep an abusive tenant locked out, use suspension and do
            NOT offboard.
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">
              Type <span className="font-mono font-bold">OFFBOARD</span> to confirm
            </label>
            <input
              type="text"
              value={confirmText}
              onChange={(e) => setConfirmText(e.target.value)}
              placeholder="OFFBOARD"
              autoComplete="off"
              className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm font-mono focus:ring-2 focus:ring-red-500 focus:border-red-500"
            />
          </div>
          {error && (
            <div role="alert" className="bg-red-100 border border-red-300 rounded-lg p-3 text-red-800">
              <strong>Offboarding failed:</strong>{" "}{error}
            </div>
          )}
        </div>

        <div className="bg-gray-50 px-5 py-4 border-t border-gray-200 rounded-b-lg flex justify-end space-x-3">
          <button
            onClick={onCancel}
            disabled={saving}
            className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={!armed || saving}
            className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 flex items-center space-x-2"
          >
            {saving ? (
              <>
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                <span>Offboarding…</span>
              </>
            ) : (
              <span>Offboard tenant</span>
            )}
          </button>
        </div>
      </div>
    </div>
  );
}
