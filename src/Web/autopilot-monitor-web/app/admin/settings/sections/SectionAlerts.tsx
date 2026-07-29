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
    opsAlertTelegramEnabled,
    opsAlertTelegramChatId,
    opsAlertTeamsEnabled,
    opsAlertTeamsWebhookUrl,
    opsAlertSlackEnabled,
    opsAlertSlackWebhookUrl,
    excessiveEventCountThreshold,
    excessiveEventAutoActionMode,
    excessiveEventAutoActionThreshold,
    excessiveEventAutoActionDurationHours,
    handleSaveOpsAlertConfig,
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
        opsAlertTelegramEnabled={opsAlertTelegramEnabled}
        opsAlertTelegramChatId={opsAlertTelegramChatId}
        opsAlertTeamsEnabled={opsAlertTeamsEnabled}
        opsAlertTeamsWebhookUrl={opsAlertTeamsWebhookUrl}
        opsAlertSlackEnabled={opsAlertSlackEnabled}
        opsAlertSlackWebhookUrl={opsAlertSlackWebhookUrl}
        excessiveEventCountThreshold={excessiveEventCountThreshold}
        excessiveEventAutoActionMode={excessiveEventAutoActionMode}
        excessiveEventAutoActionThreshold={excessiveEventAutoActionThreshold}
        excessiveEventAutoActionDurationHours={excessiveEventAutoActionDurationHours}
        onSave={handleSaveOpsAlertConfig}
      />
    </>
  );
}
