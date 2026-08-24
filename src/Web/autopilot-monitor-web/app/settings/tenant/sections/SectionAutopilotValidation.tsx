"use client";

import { useAuth } from "@/contexts/AuthContext";
import { legacyConfigured, switchAuthApp } from "@/lib/authApp";
import { DOCS_URL } from "@/utils/config";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AutopilotValidationSection from "../../components/AutopilotValidationSection";
import NotRegisteredDevicesInsights from "../../components/NotRegisteredDevicesInsights";

export function SectionAutopilotValidation() {
  const {
    config,
    canEditConfig,
    validateAutopilotDevice,
    validateCorporateIdentifier,
    validateDeviceAssociation,
    handleToggleDeviceAssociationValidation,
    validateCloudPcDevice,
    validateIntuneDeviceBinding,
    handleToggleIntuneDeviceBinding,
    handleToggleCloudPcValidation,
    saveValidationGate,
    autopilotConsentInProgress, savingSection,
    beginDeviceValidationConsentFlow, detectExistingAccess,
    appHomingFunnelActive, homingFlipped,
  } = useTenantConfig();

  const { user, getAccessToken } = useAuth();

  // Validation gates + the Entra admin-consent flow are tenant-admin territory —
  // Operators do not see this section at all.
  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800 dark:bg-amber-900/30 dark:border-amber-700 dark:text-amber-200">
        This page is available to tenant administrators only.
      </div>
    );
  }
  // DevPrep "Device association" is in Microsoft Private Preview — surface only to Global Admins
  // until it ships GA. Backend always rejects writes from non-GA callers regardless of UI.
  // TODO(devprep-followup): add vitest DOM-render test asserting toggle is hidden for
  // non-GA users and visible for GA users. Requires adding jsdom + @testing-library/react
  // to the web test setup (vitest.config.ts currently only matches *.test.ts, no JSX).
  // Tracked in memory/project_devprep_followups.md.
  const showDeviceAssociationToggle = user?.isGlobalAdmin === true;
  // Same GA gate as the DevPrep preview: only the operator can turn the binding check on
  // while its enrollment-race behaviour is still being measured.
  const showIntuneDeviceBindingToggle = user?.isGlobalAdmin === true;

  // Dual app-reg window: tenants homed on the previous app registration (homedAppClientId
  // null/absent) keep working unchanged — this is a purely informational nudge, part of the
  // incentive-driven re-consent campaign. Never a warning, never an action requirement.
  const homedOnLegacyApp = legacyConfigured() && config != null && !config.homedAppClientId;

  return (
    <>
      <TenantNotifications />
      {homingFlipped ? (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800 dark:bg-green-900/30 dark:border-green-700 dark:text-green-200">
          <p>
            Your tenant now runs on the <strong>new</strong> Autopilot Monitor app registration.
            Current sessions keep working; every next sign-in uses the new app automatically.
          </p>
          <button
            onClick={() => switchAuthApp("primary")}
            className="mt-2 px-3 py-1.5 text-sm font-medium text-white bg-green-600 rounded-lg hover:bg-green-700 transition-colors"
          >
            Sign in with the new app now
          </button>
        </div>
      ) : appHomingFunnelActive ? (
        <div className="bg-gradient-to-r from-blue-50 to-indigo-50 border-2 border-blue-300 rounded-lg p-5 shadow-sm dark:from-blue-950/40 dark:to-indigo-950/40 dark:border-blue-700/60">
          <div className="flex items-start gap-3">
            <svg className="w-6 h-6 text-blue-600 dark:text-blue-400 flex-shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
            </svg>
            <div className="text-sm text-blue-900 dark:text-blue-100">
              <p className="font-semibold text-base">
                Please switch to the new Autopilot Monitor app registration
              </p>
              <p className="mt-1.5 text-blue-800 dark:text-blue-200">
                Your tenant still runs on the previous app registration. Granting consent below (or
                running &quot;Detect existing access&quot;) approves the <strong>new</strong> app and
                switches your tenant over automatically — current sessions keep working, and new
                sign-ins use the new app. It&apos;s a one-time step and takes about a minute.
              </p>
              <p className="mt-1.5 text-blue-800 dark:text-blue-200">
                <strong>Note:</strong> granting admin consent requires an Entra ID account that can
                consent tenant-wide (e.g. <strong>Global Administrator</strong> or{" "}
                <strong>Privileged Role Administrator</strong>). The consented permission stays the
                same read-only Graph permission as before
                (<code className="text-xs bg-blue-100 dark:bg-blue-900/60 px-1 rounded">DeviceManagementServiceConfig.Read.All</code>).
              </p>
              <a
                href={`${DOCS_URL}/getting-started/portal-setup`}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1 mt-2.5 text-sm font-medium text-blue-700 hover:text-blue-900 underline dark:text-blue-300 dark:hover:text-blue-100"
              >
                See the documentation for details on the required permissions
                <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
                </svg>
              </a>
              {/* Always-visible funnel actions: a tenant whose validations are ALREADY enabled has
                  no consent button to click (the toggles are on) and the per-toggle "Detect
                  existing access" links only appear under DISABLED toggles — and would enable that
                  validation as a side effect. These two act on the existing autopilot gate
                  (idempotent when it is already on) and never touch the other toggles. */}
              <div className="mt-3 flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={() => { void beginDeviceValidationConsentFlow("autopilot"); }}
                  disabled={autopilotConsentInProgress || savingSection === "autopilotValidation"}
                  className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 rounded-lg hover:bg-blue-700 disabled:opacity-60 disabled:cursor-not-allowed transition-colors"
                >
                  Grant consent for the new app
                </button>
                <button
                  type="button"
                  onClick={() => { void detectExistingAccess("autopilot"); }}
                  disabled={autopilotConsentInProgress || savingSection === "autopilotValidation"}
                  className="text-sm font-medium text-blue-700 hover:text-blue-900 underline underline-offset-2 disabled:opacity-60 disabled:cursor-not-allowed dark:text-blue-300 dark:hover:text-blue-100"
                >
                  Detect existing access
                </button>
              </div>
            </div>
          </div>
        </div>
      ) : homedOnLegacyApp ? (
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 text-sm text-blue-800 dark:bg-blue-900/30 dark:border-blue-700 dark:text-blue-200">
          Autopilot device validation currently runs on the previous Autopilot Monitor app
          registration. It keeps working as-is — at your convenience, please re-consent to the
          new app registration; we will reach out with details as part of the migration.
        </div>
      ) : null}
      <AutopilotValidationSection
        validateAutopilotDevice={validateAutopilotDevice}
        // The gates have NO save bar: every setter call from the toggles (disable via the
        // confirm dialog, enabling the second gate while the first carries the consent) must
        // PERSIST, not just set local state — a state-only toggle silently reverts on the next
        // config load (prod report 2026-08-01: corporate identifier "kept coming back on").
        // Consent-driven enables don't come through here (onBeginConsent path).
        setValidateAutopilotDevice={(v) => { void saveValidationGate({ validateAutopilotDevice: v }); }}
        validateCorporateIdentifier={validateCorporateIdentifier}
        setValidateCorporateIdentifier={(v) => { void saveValidationGate({ validateCorporateIdentifier: v }); }}
        validateDeviceAssociation={validateDeviceAssociation}
        onToggleDeviceAssociation={handleToggleDeviceAssociationValidation}
        showDeviceAssociationToggle={showDeviceAssociationToggle}
        validateCloudPcDevice={validateCloudPcDevice}
        validateIntuneDeviceBinding={validateIntuneDeviceBinding}
        onToggleIntuneDeviceBinding={handleToggleIntuneDeviceBinding}
        showIntuneDeviceBindingToggle={showIntuneDeviceBindingToggle}
        onToggleCloudPc={handleToggleCloudPcValidation}
        autopilotConsentInProgress={autopilotConsentInProgress}
        saving={savingSection === "autopilotValidation"}
        onBeginConsent={beginDeviceValidationConsentFlow}
        onDetectExistingAccess={detectExistingAccess}
      />
      <NotRegisteredDevicesInsights getAccessToken={getAccessToken} />
    </>
  );
}
