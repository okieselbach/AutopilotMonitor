"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { AdminConfigSettingsSection } from "../../components/AdminConfigSettingsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionGlobalSettings() {
  const {
    loadingConfig, savingConfig, adminConfig,
    globalRateLimit, setGlobalRateLimit,
    userRateLimit, setUserRateLimit,
    globalAdminRateLimit, setGlobalAdminRateLimit,
    platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl,
    agentMigrateApiBaseUrl, setAgentMigrateApiBaseUrl,
    agentMigrateTenantOverridesJson, setAgentMigrateTenantOverridesJson,
    collectorIdleTimeoutMinutes, setCollectorIdleTimeoutMinutes,
    desktopDetectorNoCandidateTimeoutMinutes, setDesktopDetectorNoCandidateTimeoutMinutes,
    slaNotificationCooldownHours, setSlaNotificationCooldownHours,
    allowAgentDowngrade, setAllowAgentDowngrade,
    modernDeploymentHarmlessEventIds, setModernDeploymentHarmlessEventIds,
    enableIndexDualWrite, setEnableIndexDualWrite,
    sessionDeletionKillSwitch, setSessionDeletionKillSwitch,
    handleSaveAdminConfig, handleResetAdminConfig,
  } = useAdminConfig();

  return (
    <>
      <AdminNotifications />
      <AdminConfigSettingsSection
        loadingConfig={loadingConfig}
        savingConfig={savingConfig}
        adminConfig={adminConfig}
        globalRateLimit={globalRateLimit}
        setGlobalRateLimit={setGlobalRateLimit}
        userRateLimit={userRateLimit}
        setUserRateLimit={setUserRateLimit}
        globalAdminRateLimit={globalAdminRateLimit}
        setGlobalAdminRateLimit={setGlobalAdminRateLimit}
        platformStatsBlobSasUrl={platformStatsBlobSasUrl}
        setPlatformStatsBlobSasUrl={setPlatformStatsBlobSasUrl}
        agentMigrateApiBaseUrl={agentMigrateApiBaseUrl}
        setAgentMigrateApiBaseUrl={setAgentMigrateApiBaseUrl}
        agentMigrateTenantOverridesJson={agentMigrateTenantOverridesJson}
        setAgentMigrateTenantOverridesJson={setAgentMigrateTenantOverridesJson}
        collectorIdleTimeoutMinutes={collectorIdleTimeoutMinutes}
        setCollectorIdleTimeoutMinutes={setCollectorIdleTimeoutMinutes}
        desktopDetectorNoCandidateTimeoutMinutes={desktopDetectorNoCandidateTimeoutMinutes}
        setDesktopDetectorNoCandidateTimeoutMinutes={setDesktopDetectorNoCandidateTimeoutMinutes}
        slaNotificationCooldownHours={slaNotificationCooldownHours}
        setSlaNotificationCooldownHours={setSlaNotificationCooldownHours}
        allowAgentDowngrade={allowAgentDowngrade}
        setAllowAgentDowngrade={setAllowAgentDowngrade}
        modernDeploymentHarmlessEventIds={modernDeploymentHarmlessEventIds}
        setModernDeploymentHarmlessEventIds={setModernDeploymentHarmlessEventIds}
        enableIndexDualWrite={enableIndexDualWrite}
        setEnableIndexDualWrite={setEnableIndexDualWrite}
        sessionDeletionKillSwitch={sessionDeletionKillSwitch}
        setSessionDeletionKillSwitch={setSessionDeletionKillSwitch}
        onSave={handleSaveAdminConfig}
        onReset={handleResetAdminConfig}
      />
    </>
  );
}
