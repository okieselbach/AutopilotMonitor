"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import SaveResetBar from "../../components/SaveResetBar";
import ReadOnlyFieldset from "../../components/ReadOnlyFieldset";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";

export function SectionContact() {
  const {
    canEditConfig,
    contactEmail, setContactEmail,
    companyName, setCompanyName,
    handleSaveContact, handleResetContact,
    savingSection,
  } = useTenantConfig();

  const trimmed = contactEmail.trim();
  const looksInvalid = trimmed.length > 0 && !trimmed.includes("@");
  // Mirrors TenantConfigValidation.MaxCompanyNameLength — the backend rejects longer values.
  const companyTooLong = companyName.trim().length > 200;

  return (
    <div className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="indigo"
        iconPath="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
        title="Contact"
        subtitle="Where we reach you about the service"
        docsPath={DOCS_PATHS.contact}
      />

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
            className="mt-1 block w-full max-w-md rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-green-500 focus:ring-green-500 disabled:bg-gray-50 disabled:text-gray-600"
          />
          {looksInvalid && (
            <p className="mt-1 text-sm text-amber-600">That does not look like an email address.</p>
          )}
        </div>
        <div>
          <label htmlFor="companyName" className="block text-sm font-medium text-gray-700">
            Company
          </label>
          <input
            id="companyName"
            type="text"
            maxLength={200}
            value={companyName}
            onChange={(e) => setCompanyName(e.target.value)}
            placeholder={canEditConfig ? "Contoso Ltd." : "Not configured"}
            className="mt-1 block w-full max-w-md rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:border-green-500 focus:ring-green-500 disabled:bg-gray-50 disabled:text-gray-600"
          />
          {companyTooLong && (
            <p className="mt-1 text-sm text-amber-600">At most 200 characters.</p>
          )}
        </div>
        </ReadOnlyFieldset>

        <div className="rounded-md bg-blue-50 border border-blue-100 p-4">
          <p className="text-sm text-blue-900">
            {/* {" "} is deliberate: Turbopack eats the ambient space after an inline element
                when the following text runs over multiple lines — rendered "onlyto". */}
            Used <strong>only</strong>{" "}to reach you about this service — a technical problem affecting your tenant, a
            security matter, or a change that needs an administrator&apos;s attention. Never for marketing, and never
            shared with anyone else.
          </p>
          <p className="mt-2 text-sm text-blue-800">
            A shared team mailbox works better than a personal address. On the Community plan both fields are
            optional — leaving them empty only means we have no way to reach you before acting on a problem
            affecting your tenant.
          </p>
          <p className="mt-2 text-sm text-blue-800">
            Both are required before starting a Pro trial or moving to Pro: a paying tenant must be reachable and
            identifiable for support. The company name is how our support engineers refer to your organization.
          </p>
        </div>

        {canEditConfig && (
          <SaveResetBar
            onSave={handleSaveContact}
            onReset={handleResetContact}
            saving={savingSection === "contact"}
            canSave={!looksInvalid && !companyTooLong}
          />
        )}
      </div>
    </div>
  );
}
