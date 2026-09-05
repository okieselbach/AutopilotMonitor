"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { AdminConfigSettingsSection } from "../../components/AdminConfigSettingsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionGlobalSettings() {
  const {
    ensureAdminConfigLoaded,
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
    sessionDeletionKillSwitch, setSessionDeletionKillSwitch,
    autoApproveNewTenants, setAutoApproveNewTenants,
    selfServiceAppHomingEnabled, setSelfServiceAppHomingEnabled,
    imeMsiArchivingEnabled, setImeMsiArchivingEnabled,
    maxImeMsiDownloadSizeMB, setMaxImeMsiDownloadSizeMB,
    handleSaveAdminConfig, handleResetAdminConfig,
  } = useAdminConfig();

  useEffect(() => { ensureAdminConfigLoaded(); }, [ensureAdminConfigLoaded]);

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
        sessionDeletionKillSwitch={sessionDeletionKillSwitch}
        setSessionDeletionKillSwitch={setSessionDeletionKillSwitch}
        autoApproveNewTenants={autoApproveNewTenants}
        setAutoApproveNewTenants={setAutoApproveNewTenants}
        selfServiceAppHomingEnabled={selfServiceAppHomingEnabled}
        setSelfServiceAppHomingEnabled={setSelfServiceAppHomingEnabled}
        imeMsiArchivingEnabled={imeMsiArchivingEnabled}
        setImeMsiArchivingEnabled={setImeMsiArchivingEnabled}
        maxImeMsiDownloadSizeMB={maxImeMsiDownloadSizeMB}
        setMaxImeMsiDownloadSizeMB={setMaxImeMsiDownloadSizeMB}
        onSave={handleSaveAdminConfig}
        onReset={handleResetAdminConfig}
      />
    </>
  );
}
