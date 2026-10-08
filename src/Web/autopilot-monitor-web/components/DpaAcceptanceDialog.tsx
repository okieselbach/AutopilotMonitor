"use client";

import { useState } from "react";
import { useAuth } from "@/contexts/AuthContext";
import { api } from "@/lib/api";
import { apiErrorText, fetchOk } from "@/lib/apiClient";
import { ModalPortal } from "./ModalPortal";
import { TermsDpaAgreementText } from "./legal/TermsDpaAgreementText";

/**
 * Terms + DPA confirmation for a tenant whose first sign-in skipped the get-started tick
 * (auth/me `dpaAcceptancePending`). ProtectedRoute renders it instead of the page, so nothing
 * loads before the tick; there is no way to dismiss it other than accepting or signing out.
 */
export function DpaAcceptanceDialog() {
  const { getAccessToken, refreshUserInfo, logout } = useAuth();
  const [accepted, setAccepted] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleContinue = async () => {
    if (!accepted || saving) return;
    setSaving(true);
    setError(null);
    try {
      await fetchOk(api.auth.dpaAcceptance(), getAccessToken, { method: "POST" });
      // auth/me now answers without the flag and ProtectedRoute renders the page.
      await refreshUserInfo();
    } catch (err: unknown) {
      setError(apiErrorText(err, "Your confirmation could not be saved."));
      setSaving(false);
    }
  };

  return (
    <ModalPortal>
      <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
        <div
          role="dialog"
          aria-modal="true"
          aria-labelledby="dpa-acceptance-title"
          className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-md w-full"
        >
          <div className="p-6">
            <div className="flex items-center mb-4">
              <div className="flex-shrink-0 w-12 h-12 bg-blue-100 dark:bg-blue-900/40 rounded-full flex items-center justify-center">
                <svg className="w-6 h-6 text-blue-600 dark:text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
              </div>
              <h3 id="dpa-acceptance-title" className="ml-4 text-lg font-semibold text-gray-900 dark:text-gray-100">
                Before you continue
              </h3>
            </div>

            <p className="text-sm text-gray-700 dark:text-gray-300 mb-4">
              Please confirm the terms for your organization.
            </p>

            <label className="flex items-start gap-3 text-sm text-gray-700 dark:text-gray-300 leading-relaxed cursor-pointer select-none mb-4">
              <input
                type="checkbox"
                checked={accepted}
                onChange={e => setAccepted(e.target.checked)}
                disabled={saving}
                className="mt-0.5 h-4 w-4 shrink-0 cursor-pointer accent-blue-600"
              />
              <TermsDpaAgreementText linkClassName="text-blue-600 dark:text-blue-400 hover:underline" />
            </label>

            {error && (
              <div className="mb-4 p-3 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-lg text-sm text-red-700 dark:text-red-300">
                {error}
              </div>
            )}

            <div className="flex items-center justify-between gap-3">
              <button
                type="button"
                onClick={() => { logout().catch(() => { /* MSAL already logged the error */ }); }}
                disabled={saving}
                className="text-sm text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 disabled:opacity-50"
              >
                Sign out
              </button>
              <button
                type="button"
                onClick={handleContinue}
                disabled={!accepted || saving}
                className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 flex items-center gap-2"
              >
                {saving && <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>}
                Continue
              </button>
            </div>
          </div>
        </div>
      </div>
    </ModalPortal>
  );
}
