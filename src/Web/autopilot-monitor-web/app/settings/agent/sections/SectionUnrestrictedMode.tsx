"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import UnrestrictedModeSection from "../../components/UnrestrictedModeSection";

export function SectionUnrestrictedMode() {
  const {
    canEditConfig, config,
    unrestrictedMode, setUnrestrictedMode,
    handleSaveUnrestrictedMode,
    savingSection,
  } = useTenantConfig();

  // Security-sensitive toggle — Operators never see it (with the unified config fetch
  // they would otherwise see the GA-gate flag and get the section rendered).
  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
        This page is available to tenant administrators only.
      </div>
    );
  }

  if (!config?.unrestrictedModeEnabled) {
    return (
      <div className="bg-white rounded-lg shadow p-8 text-center">
        <p className="text-gray-500">Unrestricted Mode is not available for this tenant.</p>
      </div>
    );
  }

  return (
    <>
      <TenantNotifications />
      <UnrestrictedModeSection
        unrestrictedMode={unrestrictedMode}
        setUnrestrictedMode={setUnrestrictedMode}
        onSave={handleSaveUnrestrictedMode}
        saving={savingSection === "unrestrictedMode"}
      />
    </>
  );
}
