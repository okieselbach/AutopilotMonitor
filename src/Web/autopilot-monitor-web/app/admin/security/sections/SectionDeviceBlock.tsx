"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { DeviceBlockSection } from "../../components/DeviceBlockSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionDeviceBlock() {
  const { tenants, getAccessToken, setError, setSuccessMessage } = useAdminConfig();

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
