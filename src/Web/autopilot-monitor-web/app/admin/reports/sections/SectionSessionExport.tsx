"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { SessionExportSection } from "../../components/SessionExportSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionSessionExport() {
  const { ensureTenantsLoaded, tenants, getAccessToken } = useAdminConfig();

  useEffect(() => { ensureTenantsLoaded(); }, [ensureTenantsLoaded]);

  return (
    <>
      <AdminNotifications />
      <SessionExportSection tenants={tenants} getAccessToken={getAccessToken} />
    </>
  );
}
