"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { DiagnosticsLogPathsSection } from "../../components/DiagnosticsLogPathsSection";
import { AdminNotifications } from "../../AdminNotifications";
import { useDiagnosticsPathsCatalog } from "@/hooks/useDiagnosticsPathsCatalog";

export function SectionDiagnosticsLogPaths() {
  const { ensureAdminConfigLoaded, globalDiagPaths, setGlobalDiagPaths, loadingConfig, savingDiagPaths, adminConfig, handleSaveDiagPaths } = useAdminConfig();
  // Built-in catalog is read-only context; the editable global list stays on the admin config.
  const catalog = useDiagnosticsPathsCatalog();

  useEffect(() => { ensureAdminConfigLoaded(); }, [ensureAdminConfigLoaded]);

  return (
    <>
      <AdminNotifications />
      <DiagnosticsLogPathsSection
        globalDiagPaths={globalDiagPaths}
        setGlobalDiagPaths={setGlobalDiagPaths}
        builtInSections={catalog.builtIn}
        builtInLoading={catalog.loading}
        loadingConfig={loadingConfig}
        savingDiagPaths={savingDiagPaths}
        adminConfigExists={!!adminConfig}
        onSave={handleSaveDiagPaths}
      />
    </>
  );
}
