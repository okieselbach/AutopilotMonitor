"use client";

import { useGlobalAdminUi } from "@/hooks/useGlobalAdminUi";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import NotificationsSection from "../../components/NotificationsSection";

export function SectionNotifications() {
  const {
    canEditConfig,
    notificationChannels, setNotificationChannels,
    handleTestChannel, testingChannelId, testChannelResult,
    handleSaveNotifications, handleResetNotifications,
    savingSection,
  } = useTenantConfig();

  // Telegram channels deliver through the platform-owned bot, so only a Global Admin may
  // configure one. The server enforces the same rule (TenantConfigValidation) — hiding the
  // option here is convenience, not the control. Follows the Global-Admin VIEW, so switching it
  // off (or presenting in demo mode) yields the real tenant-admin dropdown.
  const showTelegramProvider = useGlobalAdminUi();

  return (
    <>
      <TenantNotifications />
      <NotificationsSection
        channels={notificationChannels}
        setChannels={setNotificationChannels}
        onTestChannel={handleTestChannel}
        testingChannelId={testingChannelId}
        testChannelResult={testChannelResult}
        onSave={handleSaveNotifications}
        onReset={handleResetNotifications}
        saving={savingSection === "notifications"}
        readOnly={!canEditConfig}
        showTelegramProvider={showTelegramProvider}
      />
    </>
  );
}
