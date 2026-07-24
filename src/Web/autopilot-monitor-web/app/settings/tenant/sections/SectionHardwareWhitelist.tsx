"use client";

import { useCallback } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import HardwareWhitelistSection from "../../components/HardwareWhitelistSection";
import HardwareRejectionInsights from "../../components/HardwareRejectionInsights";

function parseList(csv: string): string[] {
  return csv.split(",").map((s) => s.trim()).filter(Boolean);
}

function joinList(items: string[]): string {
  return items.join(",");
}

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

  const handleAddManufacturer = useCallback((value: string) => {
    const items = parseList(manufacturerWhitelist);
    if (!items.some((i) => i.toLowerCase() === value.toLowerCase())) {
      setManufacturerWhitelist(joinList([...items, value]));
    }
  }, [manufacturerWhitelist, setManufacturerWhitelist]);

  const handleAddModel = useCallback((value: string) => {
    const items = parseList(modelWhitelist);
    if (!items.some((i) => i.toLowerCase() === value.toLowerCase())) {
      setModelWhitelist(joinList([...items, value]));
    }
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
    </>
  );
}
