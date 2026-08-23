"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import DiagnosticsSection from "../../components/DiagnosticsSection";
import { useDiagnosticsPathsCatalog } from "@/hooks/useDiagnosticsPathsCatalog";

export function SectionDiagnostics() {
  const {
    config,
    canEditConfig,
    diagnosticsBlobSasUrl, setDiagnosticsBlobSasUrl,
    diagnosticsUploadMode, setDiagnosticsUploadMode,
    diagnosticsUploadDestination, setDiagnosticsUploadDestination,
    tenantDiagPaths, setTenantDiagPaths,
    newDiagPath, setNewDiagPath,
    newDiagDesc, setNewDiagDesc,
    unrestrictedMode,
    handleSaveDiagnostics, handleResetDiagnostics,
    savingSection,
  } = useTenantConfig();
  // Built-in sections + platform-wide paths (MemberRead) — read-only context for every role.
  const catalog = useDiagnosticsPathsCatalog();

  return (
    <>
      <TenantNotifications />
      <DiagnosticsSection
        diagnosticsBlobSasUrl={diagnosticsBlobSasUrl}
        setDiagnosticsBlobSasUrl={setDiagnosticsBlobSasUrl}
        diagnosticsUploadMode={diagnosticsUploadMode}
        setDiagnosticsUploadMode={setDiagnosticsUploadMode}
        diagnosticsUploadDestination={diagnosticsUploadDestination}
        setDiagnosticsUploadDestination={setDiagnosticsUploadDestination}
        tenantDiagPaths={tenantDiagPaths}
        setTenantDiagPaths={setTenantDiagPaths}
        globalDiagPaths={catalog.globalPaths}
        builtInSections={catalog.builtIn}
        builtInLoading={catalog.loading}
        // PERSISTED value: the provider survives section navigation, so the Analyzers page's
        // draft toggle may differ from what the agent actually receives.
        realmJoinWatcherEnabled={config?.enableRealmJoinWatcher ?? false}
        newDiagPath={newDiagPath}
        setNewDiagPath={setNewDiagPath}
        newDiagDesc={newDiagDesc}
        setNewDiagDesc={setNewDiagDesc}
        unrestrictedMode={unrestrictedMode}
        onSave={handleSaveDiagnostics}
        onReset={handleResetDiagnostics}
        saving={savingSection === "diagnostics"}
        readOnly={!canEditConfig}
      />
    </>
  );
}
