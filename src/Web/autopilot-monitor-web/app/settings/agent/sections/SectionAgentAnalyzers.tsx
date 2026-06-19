"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AgentAnalyzersSection from "../../components/AgentAnalyzersSection";

export function SectionAgentAnalyzers() {
  const {
    enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer,
    localAdminAllowedAccounts, setLocalAdminAllowedAccounts,
    newAllowedAccount, setNewAllowedAccount,
    enableSoftwareInventoryAnalyzer, setEnableSoftwareInventoryAnalyzer,
    enableIntegrityBypassAnalyzer, setEnableIntegrityBypassAnalyzer,
    enableRealmJoinWatcher, setEnableRealmJoinWatcher,
    keepAwakeDuringUserEsp, setKeepAwakeDuringUserEsp,
    enableConsoleBypassDetection, setEnableConsoleBypassDetection,
    handleSaveAgentAnalyzers, handleResetAgentAnalyzers,
    savingSection,
  } = useTenantConfig();

  return (
    <>
      <TenantNotifications />
      <AgentAnalyzersSection
        enableLocalAdminAnalyzer={enableLocalAdminAnalyzer}
        setEnableLocalAdminAnalyzer={setEnableLocalAdminAnalyzer}
        localAdminAllowedAccounts={localAdminAllowedAccounts}
        setLocalAdminAllowedAccounts={setLocalAdminAllowedAccounts}
        newAllowedAccount={newAllowedAccount}
        setNewAllowedAccount={setNewAllowedAccount}
        enableSoftwareInventoryAnalyzer={enableSoftwareInventoryAnalyzer}
        setEnableSoftwareInventoryAnalyzer={setEnableSoftwareInventoryAnalyzer}
        enableIntegrityBypassAnalyzer={enableIntegrityBypassAnalyzer}
        setEnableIntegrityBypassAnalyzer={setEnableIntegrityBypassAnalyzer}
        enableRealmJoinWatcher={enableRealmJoinWatcher}
        setEnableRealmJoinWatcher={setEnableRealmJoinWatcher}
        keepAwakeDuringUserEsp={keepAwakeDuringUserEsp}
        setKeepAwakeDuringUserEsp={setKeepAwakeDuringUserEsp}
        enableConsoleBypassDetection={enableConsoleBypassDetection}
        setEnableConsoleBypassDetection={setEnableConsoleBypassDetection}
        onSave={handleSaveAgentAnalyzers}
        onReset={handleResetAgentAnalyzers}
        saving={savingSection === "agentAnalyzers"}
      />
    </>
  );
}
