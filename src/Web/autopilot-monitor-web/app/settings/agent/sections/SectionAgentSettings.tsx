"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AgentSettingsSection from "../../components/AgentSettingsSection";

export function SectionAgentSettings() {
  const {
    canEditConfig,
    enablePerformanceCollector, setEnablePerformanceCollector,
    performanceCollectorInterval, setPerformanceCollectorInterval,
    helloWaitTimeoutSeconds, setHelloWaitTimeoutSeconds,
    enableRealmJoinWatcher, setEnableRealmJoinWatcher,
    selfDestructOnComplete, setSelfDestructOnComplete,
    keepLogFile, setKeepLogFile,
    rebootOnComplete, setRebootOnComplete,
    rebootDelaySeconds, setRebootDelaySeconds,
    enableGeoLocation, setEnableGeoLocation,
    enableTimezoneAutoSet, setEnableTimezoneAutoSet,
    enableDoGroupIdAutoSet, setEnableDoGroupIdAutoSet,
    keepAwakeDuringUserEsp, setKeepAwakeDuringUserEsp,
    enableImeMatchLog, setEnableImeMatchLog,
    enableGatherRuleDebugLog, setEnableGatherRuleDebugLog,
    logLevel, setLogLevel,
    showScriptOutput, setShowScriptOutput,
    showEnrollmentSummary, setShowEnrollmentSummary,
    enrollmentSummaryTimeoutSeconds, setEnrollmentSummaryTimeoutSeconds,
    enrollmentSummaryBrandingImageUrl, setEnrollmentSummaryBrandingImageUrl,
    enrollmentSummaryLaunchRetrySeconds, setEnrollmentSummaryLaunchRetrySeconds,
    handleSaveAgentSettings, handleResetAgentSettings,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <AgentSettingsSection
        enablePerformanceCollector={enablePerformanceCollector}
        setEnablePerformanceCollector={setEnablePerformanceCollector}
        performanceCollectorInterval={performanceCollectorInterval}
        setPerformanceCollectorInterval={setPerformanceCollectorInterval}
        helloWaitTimeoutSeconds={helloWaitTimeoutSeconds}
        setHelloWaitTimeoutSeconds={setHelloWaitTimeoutSeconds}
        enableRealmJoinWatcher={enableRealmJoinWatcher}
        setEnableRealmJoinWatcher={setEnableRealmJoinWatcher}
        selfDestructOnComplete={selfDestructOnComplete}
        setSelfDestructOnComplete={setSelfDestructOnComplete}
        keepLogFile={keepLogFile}
        setKeepLogFile={setKeepLogFile}
        rebootOnComplete={rebootOnComplete}
        setRebootOnComplete={setRebootOnComplete}
        rebootDelaySeconds={rebootDelaySeconds}
        setRebootDelaySeconds={setRebootDelaySeconds}
        enableGeoLocation={enableGeoLocation}
        setEnableGeoLocation={setEnableGeoLocation}
        enableTimezoneAutoSet={enableTimezoneAutoSet}
        setEnableTimezoneAutoSet={setEnableTimezoneAutoSet}
        enableDoGroupIdAutoSet={enableDoGroupIdAutoSet}
        setEnableDoGroupIdAutoSet={setEnableDoGroupIdAutoSet}
        keepAwakeDuringUserEsp={keepAwakeDuringUserEsp}
        setKeepAwakeDuringUserEsp={setKeepAwakeDuringUserEsp}
        enableImeMatchLog={enableImeMatchLog}
        setEnableImeMatchLog={setEnableImeMatchLog}
        enableGatherRuleDebugLog={enableGatherRuleDebugLog}
        setEnableGatherRuleDebugLog={setEnableGatherRuleDebugLog}
        logLevel={logLevel}
        setLogLevel={setLogLevel}
        showScriptOutput={showScriptOutput}
        setShowScriptOutput={setShowScriptOutput}
        showEnrollmentSummary={showEnrollmentSummary}
        setShowEnrollmentSummary={setShowEnrollmentSummary}
        enrollmentSummaryTimeoutSeconds={enrollmentSummaryTimeoutSeconds}
        setEnrollmentSummaryTimeoutSeconds={setEnrollmentSummaryTimeoutSeconds}
        enrollmentSummaryBrandingImageUrl={enrollmentSummaryBrandingImageUrl}
        setEnrollmentSummaryBrandingImageUrl={setEnrollmentSummaryBrandingImageUrl}
        enrollmentSummaryLaunchRetrySeconds={enrollmentSummaryLaunchRetrySeconds}
        setEnrollmentSummaryLaunchRetrySeconds={setEnrollmentSummaryLaunchRetrySeconds}
        onSave={handleSaveAgentSettings}
        onReset={handleResetAgentSettings}
        saving={savingSection === "agentSettings"}
        readOnly={!canEditConfig}
      />
    </>
  );
}
