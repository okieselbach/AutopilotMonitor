"use client";

import { useAuth } from "@/contexts/AuthContext";
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
  // option here is convenience, not the control.
  const { user } = useAuth();
  const showTelegramProvider = user?.isGlobalAdmin === true;

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
