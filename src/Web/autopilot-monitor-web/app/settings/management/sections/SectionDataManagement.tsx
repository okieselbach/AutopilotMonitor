"use client";

import { useGlobalAdminUi } from "@/hooks/useGlobalAdminUi";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import DataManagementSection from "../../components/DataManagementSection";

export function SectionDataManagement() {
  const {
    canEditConfig,
    dataRetentionDays, setDataRetentionDays,
    sessionTimeoutHours, setSessionTimeoutHours,
    editionInfo,
    handleSaveDataManagement, handleResetDataManagement,
    savingSection,
  } = useTenantConfig();

  // The escape hatch that keeps an out-of-plan retention value editable is Global-Admin-only, and
  // follows the Global-Admin VIEW: switched off, the field locks exactly as it does for a tenant
  // admin. The server enforces the cap regardless (TenantConfigValidation).
  const isGlobalAdminView = useGlobalAdminUi();

  return (
    <>
      <TenantNotifications />
      <DataManagementSection
        dataRetentionDays={dataRetentionDays}
        setDataRetentionDays={setDataRetentionDays}
        sessionTimeoutHours={sessionTimeoutHours}
        setSessionTimeoutHours={setSessionTimeoutHours}
        isGlobalAdmin={isGlobalAdminView}
        retentionCapDays={editionInfo.entitlements.retentionCapDays}
        onSave={handleSaveDataManagement}
        onReset={handleResetDataManagement}
        saving={savingSection === "dataManagement"}
        readOnly={!canEditConfig}
      />
    </>
  );
}
