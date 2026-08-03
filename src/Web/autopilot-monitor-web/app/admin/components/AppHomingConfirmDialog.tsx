"use client";

import { useState } from "react";

interface AppHomingConfirmDialogProps {
  tenantLabel: string;
  target: "primary" | "legacy";
  entraAppRolesEnabled: boolean;
  saving: boolean;
  /** Failure of the homing POST — rendered inside the dialog; a page-level
   *  banner would sit behind this overlay (z-[60]) and never be seen. */
  error: string | null;
  onCancel: () => void;
  onConfirm: (force: boolean) => void;
}

/**
 * GA confirmation for the app-registration homing flip. Renders above the tenant editor
 * modal (z-[60] vs its z-50). The Force checkbox skips the backend's consent probe —
 * needed e.g. for a tenant that consented delegated-only, or to revert with certainty.
 */
export function AppHomingConfirmDialog({
  tenantLabel,
  target,
  entraAppRolesEnabled,
  saving,
  error,
  onCancel,
  onConfirm,
}: AppHomingConfirmDialogProps) {
  const [force, setForce] = useState(false);
  const toPrimary = target === "primary";

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-[60] p-4">
      <div className="bg-white rounded-lg shadow-xl max-w-lg w-full">
        <div className={`p-5 rounded-t-lg text-white ${toPrimary ? "bg-sky-600" : "bg-amber-600"}`}>
          <h2 className="text-lg font-bold">
            {toPrimary ? "Switch to the new app registration" : "Revert to the legacy app registration"}
          </h2>
          <p className="text-sm opacity-90 mt-0.5">{tenantLabel}</p>
        </div>

        <div className="p-5 space-y-3 text-sm text-gray-700">
          <p>
            Graph tokens and consent URLs for this tenant will be issued by the{" "}
            <strong>{toPrimary ? "new" : "legacy"}</strong> app registration from now on. Signed-in
            users keep their sessions; every <strong>next sign-in</strong> uses the{" "}
            {toPrimary ? "new" : "legacy"} app automatically.
          </p>
          {toPrimary && !force && (
            <p className="text-gray-500">
              The backend verifies first that the new app is admin-consented in the tenant — the
              switch is rejected if it is not.
            </p>
          )}
          {entraAppRolesEnabled && (
            <div className="bg-amber-50 border border-amber-200 rounded-lg p-3 text-amber-800">
              <strong>Entra app roles are enabled for this tenant.</strong> Role assignments live on
              the enterprise app of the current registration — re-assign them on the{" "}
              {toPrimary ? "new" : "legacy"} enterprise app or those users lose their role claims at
              next sign-in.
            </div>
          )}
          <label className="flex items-start space-x-2 cursor-pointer pt-1">
            <input
              type="checkbox"
              checked={force}
              onChange={(e) => setForce(e.target.checked)}
              className="mt-0.5 w-4 h-4 text-red-600 border-gray-300 rounded focus:ring-red-500"
            />
            <span>
              <span className="font-medium text-gray-800">Force</span>
              <span className="block text-xs text-red-600 mt-0.5">
                Skips the consent verification. A wrong switch breaks the tenant&apos;s Graph
                features until consent is granted or the homing is reverted.
              </span>
            </span>
          </label>
          {error && (
            <div role="alert" className="bg-red-100 border border-red-300 rounded-lg p-3 text-red-800">
              <strong>Switch failed:</strong>{" "}{error}
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
            onClick={() => onConfirm(force)}
            disabled={saving}
            className={`px-4 py-2 text-white rounded-md disabled:opacity-50 flex items-center space-x-2 ${
              toPrimary ? "bg-sky-600 hover:bg-sky-700" : "bg-amber-600 hover:bg-amber-700"
            }`}
          >
            {saving ? (
              <>
                <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                <span>Verifying &amp; switching…</span>
              </>
            ) : (
              <span>{toPrimary ? "Switch to new app" : "Revert to legacy app"}</span>
            )}
          </button>
        </div>
      </div>
    </div>
  );
}
