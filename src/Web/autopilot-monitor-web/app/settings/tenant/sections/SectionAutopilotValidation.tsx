"use client";

import { useAuth } from "@/contexts/AuthContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AutopilotValidationSection from "../../components/AutopilotValidationSection";
import NotRegisteredDevicesInsights from "../../components/NotRegisteredDevicesInsights";

export function SectionAutopilotValidation() {
  const {
    canEditConfig,
    validateAutopilotDevice, setValidateAutopilotDevice,
    validateCorporateIdentifier, setValidateCorporateIdentifier,
    validateDeviceAssociation,
    handleToggleDeviceAssociationValidation,
    autopilotConsentInProgress, savingSection,
    beginDeviceValidationConsentFlow, detectExistingAccess,
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

  return (
    <>
      <TenantNotifications />
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
