"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { OpsAlertRulesSection } from "../../components/OpsAlertRulesSection";
import { AdminNotifications } from "../../AdminNotifications";
import { PushDevicesPanel } from "@/components/push/PushDevicesPanel";
import { hasEnabledPushChannel } from "@/lib/pushPortal";
import { useAuth } from "@/contexts/AuthContext";
import { useChannelHealth } from "@/hooks/useChannelHealth";
import { api } from "@/lib/api";

export function SectionAlerts() {
  const {
    ensureAdminConfigLoaded,
    loadingConfig,
    savingOpsAlerts,
    adminConfig,
    opsAlertRules,
    opsNotificationChannels,
    excessiveEventCountThreshold,
    excessiveEventAutoActionMode,
    excessiveEventAutoActionThreshold,
    excessiveEventAutoActionDurationHours,
    handleSaveOpsAlertConfig,
    handleTestOpsChannel,
    testingOpsChannelId,
    testOpsChannelResult,
  } = useAdminConfig();

  useEffect(() => { ensureAdminConfigLoaded(); }, [ensureAdminConfigLoaded]);

  // Delivery status of the saved ops channels — Global Admins only, like "Send Test". Re-read
  // after a load/save (the saved list changes identity) and after every test.
  const { user } = useAuth();
  const channelHealth = useChannelHealth(
    user?.isGlobalAdmin === true ? api.globalConfig.opsChannelHealth() : null,
    opsNotificationChannels,
    testOpsChannelResult,
  );

  return (
    <>
      <AdminNotifications />
      <OpsAlertRulesSection
        loadingConfig={loadingConfig}
        savingOpsAlerts={savingOpsAlerts}
        adminConfigExists={!!adminConfig}
        opsAlertRules={opsAlertRules}
        opsNotificationChannels={opsNotificationChannels}
        excessiveEventCountThreshold={excessiveEventCountThreshold}
        excessiveEventAutoActionMode={excessiveEventAutoActionMode}
        excessiveEventAutoActionThreshold={excessiveEventAutoActionThreshold}
        excessiveEventAutoActionDurationHours={excessiveEventAutoActionDurationHours}
        onSave={handleSaveOpsAlertConfig}
        onTestChannel={handleTestOpsChannel}
        testingChannelId={testingOpsChannelId}
        testChannelResult={testOpsChannelResult}
        channelHealth={channelHealth}
      />
      {/* Platform Push devices (plan push-relay K4): only once the SAVED ops channel list carries an
          enabled Push channel — opsNotificationChannels mirrors the stored config, the editor above
          keeps its own draft. */}
      {hasEnabledPushChannel(opsNotificationChannels) && <PushDevicesPanel scope="platform" />}
    </>
  );
}
