"use client";

import { useAuth } from "@/contexts/AuthContext";
import { legacyConfigured, switchAuthApp } from "@/lib/authApp";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AutopilotValidationSection from "../../components/AutopilotValidationSection";
import NotRegisteredDevicesInsights from "../../components/NotRegisteredDevicesInsights";

export function SectionAutopilotValidation() {
  const {
    config,
    canEditConfig,
    validateAutopilotDevice, setValidateAutopilotDevice,
    validateCorporateIdentifier, setValidateCorporateIdentifier,
    validateDeviceAssociation,
    handleToggleDeviceAssociationValidation,
    autopilotConsentInProgress, savingSection,
    beginDeviceValidationConsentFlow, detectExistingAccess,
    appHomingFunnelActive, homingFlipped,
  } = useTenantConfig();

  const { user, getAccessToken } = useAuth();

  // Validation gates + the Entra admin-consent flow are tenant-admin territory —
  // Operators do not see this section at all.
  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
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

  // Dual app-reg window: tenants homed on the previous app registration (homedAppClientId
  // null/absent) keep working unchanged — this is a purely informational nudge, part of the
  // incentive-driven re-consent campaign. Never a warning, never an action requirement.
  const homedOnLegacyApp = legacyConfigured() && config != null && !config.homedAppClientId;

  return (
    <>
      <TenantNotifications />
      {homingFlipped ? (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">
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
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 text-sm text-blue-800">
          Autopilot device validation currently runs on the previous Autopilot Monitor app
          registration. Granting consent below (or running &quot;Detect existing access&quot;)
          approves the <strong>new</strong> app registration and switches your tenant over
          automatically — current sessions keep working, and new sign-ins use the new app.
        </div>
      ) : homedOnLegacyApp ? (
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 text-sm text-blue-800">
          Autopilot device validation currently runs on the previous Autopilot Monitor app
          registration. It keeps working as-is — at your convenience, please re-consent to the
          new app registration; we will reach out with details as part of the migration.
        </div>
      ) : null}
      <AutopilotValidationSection
        validateAutopilotDevice={validateAutopilotDevice}
        setValidateAutopilotDevice={setValidateAutopilotDevice}
        validateCorporateIdentifier={validateCorporateIdentifier}
        setValidateCorporateIdentifier={setValidateCorporateIdentifier}
        validateDeviceAssociation={validateDeviceAssociation}
        onToggleDeviceAssociation={handleToggleDeviceAssociationValidation}
        showDeviceAssociationToggle={showDeviceAssociationToggle}
        autopilotConsentInProgress={autopilotConsentInProgress}
        saving={savingSection === "autopilotValidation"}
        onBeginConsent={beginDeviceValidationConsentFlow}
        onDetectExistingAccess={detectExistingAccess}
      />
      <NotRegisteredDevicesInsights getAccessToken={getAccessToken} />
    </>
  );
}
