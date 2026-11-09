"use client";

import { useCallback } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import HardwareWhitelistSection from "../../components/HardwareWhitelistSection";
import HardwareRejectionInsights from "../../components/HardwareRejectionInsights";
import TpmIncompatibleDevicesInsights from "../../components/TpmIncompatibleDevicesInsights";
import { addWhitelistEntry } from "@/lib/hardwareWhitelist";

export function SectionHardwareWhitelist() {
  const { getAccessToken } = useAuth();
  const {
    canEditConfig,
    manufacturerWhitelist, setManufacturerWhitelist,
    modelWhitelist, setModelWhitelist,
    webhookNotifyOnHardwareRejection, setWebhookNotifyOnHardwareRejection,
    notificationChannels,
    handleSaveHardwareWhitelist,
    savingSection,
    unsavedSections,
  } = useTenantConfig();

  const hasWebhook = notificationChannels.some((c) => c.enabled && (c.url ?? "").length > 0);

  // The insights pass an entry from reportedValueEntry: the reported value as one literal
  // pattern (no wildcard, a ',' cannot split the list); addWhitelistEntry skips duplicates.
  const handleAddManufacturer = useCallback((value: string) => {
    setManufacturerWhitelist(addWhitelistEntry(manufacturerWhitelist, value));
  }, [manufacturerWhitelist, setManufacturerWhitelist]);

  const handleAddModel = useCallback((value: string) => {
    setModelWhitelist(addWhitelistEntry(modelWhitelist, value));
  }, [modelWhitelist, setModelWhitelist]);

  return (
    <>
      <TenantNotifications />
      <HardwareWhitelistSection
        manufacturerWhitelist={manufacturerWhitelist}
        setManufacturerWhitelist={setManufacturerWhitelist}
        modelWhitelist={modelWhitelist}
        setModelWhitelist={setModelWhitelist}
        onSave={handleSaveHardwareWhitelist}
        saving={savingSection === "hardwareWhitelist"}
        unsaved={unsavedSections.has("hardwareWhitelist")}
        readOnly={!canEditConfig}
      />
      <HardwareRejectionInsights
        getAccessToken={getAccessToken}
        onAddManufacturer={canEditConfig ? handleAddManufacturer : undefined}
        onAddModel={canEditConfig ? handleAddModel : undefined}
        webhookNotifyOnHardwareRejection={webhookNotifyOnHardwareRejection}
        onToggleNotification={setWebhookNotifyOnHardwareRejection}
        hasWebhook={hasWebhook}
        readOnly={!canEditConfig}
      />
      <TpmIncompatibleDevicesInsights getAccessToken={getAccessToken} />
    </>
  );
}
