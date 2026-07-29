"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { DeviceBlockSection } from "../../components/DeviceBlockSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionDeviceBlock() {
  const { ensureTenantsLoaded, tenants, getAccessToken, setError, setSuccessMessage } = useAdminConfig();

  useEffect(() => { ensureTenantsLoaded(); }, [ensureTenantsLoaded]);

  return (
    <>
      <AdminNotifications />
      <DeviceBlockSection
        tenants={tenants}
        getAccessToken={getAccessToken}
        setError={setError}
        setSuccessMessage={setSuccessMessage}
      />
    </>
  );
}
