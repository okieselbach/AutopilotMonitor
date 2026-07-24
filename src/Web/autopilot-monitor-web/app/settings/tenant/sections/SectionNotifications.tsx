"use client";

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
      />
    </>
  );
}
