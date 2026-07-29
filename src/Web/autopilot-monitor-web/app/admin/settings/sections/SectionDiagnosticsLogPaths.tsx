"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { DiagnosticsLogPathsSection } from "../../components/DiagnosticsLogPathsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionDiagnosticsLogPaths() {
  const { ensureAdminConfigLoaded, globalDiagPaths, setGlobalDiagPaths, loadingConfig, savingDiagPaths, adminConfig, handleSaveDiagPaths } = useAdminConfig();

  useEffect(() => { ensureAdminConfigLoaded(); }, [ensureAdminConfigLoaded]);

  return (
    <>
      <AdminNotifications />
      <DiagnosticsLogPathsSection
        globalDiagPaths={globalDiagPaths}
        setGlobalDiagPaths={setGlobalDiagPaths}
        loadingConfig={loadingConfig}
        savingDiagPaths={savingDiagPaths}
        adminConfigExists={!!adminConfig}
        onSave={handleSaveDiagPaths}
      />
    </>
  );
}
