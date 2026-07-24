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
    selfDestructOnComplete, setSelfDestructOnComplete,
    keepLogFile, setKeepLogFile,
    rebootOnComplete, setRebootOnComplete,
    rebootDelaySeconds, setRebootDelaySeconds,
    enableGeoLocation, setEnableGeoLocation,
    enableTimezoneAutoSet, setEnableTimezoneAutoSet,
    enableImeMatchLog, setEnableImeMatchLog,
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
        enableImeMatchLog={enableImeMatchLog}
        setEnableImeMatchLog={setEnableImeMatchLog}
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
