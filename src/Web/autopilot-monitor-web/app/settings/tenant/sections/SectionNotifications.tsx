"use client";

import { useGlobalAdminUi } from "@/hooks/useGlobalAdminUi";
import { useAuth } from "@/contexts/AuthContext";
import { PushDevicesPanel } from "@/components/push/PushDevicesPanel";
import { hasEnabledPushChannel } from "@/lib/pushPortal";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import NotificationsSection from "../../components/NotificationsSection";

export function SectionNotifications() {
  const { user } = useAuth();
  const {
    config,
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

  // Push devices (plan push-relay K4): the SAVED config decides, not the draft — a Push channel
  // added but not yet saved has nothing to pair against on the server. Pairing is open to
  // Admins and Operators of the tenant (a Global Admin without a member row is refused by the
  // server with a hint), so the panel sits outside the read-only fieldset above.
  const canPairDevices =
    user?.isTenantAdmin === true || user?.role === "Admin" || user?.role === "Operator" || user?.isGlobalAdmin === true;
  const showPushDevices = canPairDevices && hasEnabledPushChannel(config?.notificationChannelsJson);

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
      {showPushDevices && <PushDevicesPanel scope="tenant" />}
    </>
  );
}
