"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AgentParametersSection from "../../components/AgentParametersSection";
import AgentCollectorsSection from "../../components/AgentCollectorsSection";

export function SectionAgentSettings() {
  const {
    canEditConfig,
    enablePerformanceCollector, setEnablePerformanceCollector,
    performanceCollectorInterval, setPerformanceCollectorInterval,
    helloWaitTimeoutSeconds, setHelloWaitTimeoutSeconds,
    enableRealmJoinWatcher, setEnableRealmJoinWatcher,
    enableConfigMgrWatcher, setEnableConfigMgrWatcher,
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
    handleSaveAgentParameters,
    handleSaveAgentCollectors,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <AgentParametersSection
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
        onSave={handleSaveAgentParameters}
        saving={savingSection === "agentParameters"}
        readOnly={!canEditConfig}
      />
      <AgentCollectorsSection
        enablePerformanceCollector={enablePerformanceCollector}
        setEnablePerformanceCollector={setEnablePerformanceCollector}
        performanceCollectorInterval={performanceCollectorInterval}
        setPerformanceCollectorInterval={setPerformanceCollectorInterval}
        helloWaitTimeoutSeconds={helloWaitTimeoutSeconds}
        setHelloWaitTimeoutSeconds={setHelloWaitTimeoutSeconds}
        enableRealmJoinWatcher={enableRealmJoinWatcher}
        setEnableRealmJoinWatcher={setEnableRealmJoinWatcher}
        enableConfigMgrWatcher={enableConfigMgrWatcher}
        setEnableConfigMgrWatcher={setEnableConfigMgrWatcher}
        onSave={handleSaveAgentCollectors}
        saving={savingSection === "agentCollectors"}
        readOnly={!canEditConfig}
      />
    </>
  );
}
