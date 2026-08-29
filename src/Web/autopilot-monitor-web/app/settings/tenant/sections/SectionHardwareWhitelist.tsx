"use client";

import { useCallback } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import HardwareWhitelistSection from "../../components/HardwareWhitelistSection";
import HardwareRejectionInsights from "../../components/HardwareRejectionInsights";
import TpmIncompatibleDevicesInsights from "../../components/TpmIncompatibleDevicesInsights";
import { addWhitelistEntry } from "../../lib/hardwareWhitelist";

export function SectionHardwareWhitelist() {
  const { getAccessToken } = useAuth();
  const {
    canEditConfig,
    manufacturerWhitelist, setManufacturerWhitelist,
    modelWhitelist, setModelWhitelist,
    webhookNotifyOnHardwareRejection, setWebhookNotifyOnHardwareRejection,
    notificationChannels,
    handleSaveHardwareWhitelist, handleResetHardwareWhitelist,
    savingSection,
  } = useTenantConfig();

  const hasWebhook = notificationChannels.some((c) => c.enabled && (c.url ?? "").length > 0);

  // Values come from UNAUTHENTICATED distress signals — addWhitelistEntry guarantees each
  // click adds exactly one pattern (a ',' inside the value cannot split the list).
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
        onReset={handleResetHardwareWhitelist}
        saving={savingSection === "hardwareWhitelist"}
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
