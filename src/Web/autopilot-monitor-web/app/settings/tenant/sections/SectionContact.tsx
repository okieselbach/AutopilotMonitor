"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import SaveResetBar from "../../components/SaveResetBar";
import ReadOnlyFieldset from "../../components/ReadOnlyFieldset";

export function SectionContact() {
  const {
    canEditConfig,
    contactEmail, setContactEmail,
    handleSaveContact, handleResetContact,
    savingSection,
  } = useTenantConfig();

  const trimmed = contactEmail.trim();
  const looksInvalid = trimmed.length > 0 && !trimmed.includes("@");

  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-indigo-50 to-blue-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Contact</h2>
            <p className="text-sm text-gray-500 mt-1">Where we reach you about the service</p>
          </div>
        </div>
      </div>

      <div className="p-6 space-y-4">
        <ReadOnlyFieldset readOnly={!canEditConfig}>
        <div>
          <label htmlFor="contactEmail" className="block text-sm font-medium text-gray-700">
            Contact email address
          </label>
          <input
            id="contactEmail"
            type="email"
            value={contactEmail}
            onChange={(e) => setContactEmail(e.target.value)}
            placeholder={canEditConfig ? "it-operations@contoso.com" : "Not configured"}
            className="mt-1 block w-full max-w-md rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-indigo-500 focus:ring-indigo-500 disabled:bg-gray-50 disabled:text-gray-600"
          />
          {looksInvalid && (
            <p className="mt-1 text-sm text-amber-600">That does not look like an email address.</p>
          )}
        </div>
        </ReadOnlyFieldset>

        <div className="rounded-md bg-blue-50 border border-blue-100 p-4">
          <p className="text-sm text-blue-900">
            Used <strong>only</strong> to reach you about this service — a technical problem affecting your tenant, a
            security matter, or a change that needs an administrator&apos;s attention. Never for marketing, and never
            shared with anyone else.
          </p>
          <p className="mt-2 text-sm text-blue-800">
            A shared team mailbox works better than a personal address. Leaving this empty is fine — it only means we
            have no way to reach you before acting on a problem affecting your tenant.
          </p>
        </div>

        {canEditConfig && (
          <SaveResetBar
            onSave={handleSaveContact}
            onReset={handleResetContact}
            saving={savingSection === "contact"}
            canSave={!looksInvalid}
          />
        )}
      </div>
    </div>
  );
}
