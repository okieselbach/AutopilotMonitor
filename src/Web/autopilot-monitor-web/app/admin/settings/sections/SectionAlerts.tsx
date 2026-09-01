"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { OpsAlertRulesSection } from "../../components/OpsAlertRulesSection";
import { AdminNotifications } from "../../AdminNotifications";

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
      />
    </>
  );
}
