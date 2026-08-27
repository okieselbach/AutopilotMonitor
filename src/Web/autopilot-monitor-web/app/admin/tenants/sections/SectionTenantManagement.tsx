"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../../AdminConfigContext";
import { TenantManagementSection } from "../../components/TenantManagementSection";

export function SectionTenantManagement() {
  const {
    ensureTenantsLoaded,
    tenants,
    loadingTenants,
    fetchTenants,
    previewApproved,
    setPreviewApproved,
    notificationEmails,
    setNotificationEmails,
    setTenants,
    getAccessToken,
    setError,
    setSuccessMessage,
  } = useAdminConfig();

  useEffect(() => { ensureTenantsLoaded(); }, [ensureTenantsLoaded]);

  return (
    <TenantManagementSection
      tenants={tenants}
      loadingTenants={loadingTenants}
      fetchTenants={fetchTenants}
      previewApproved={previewApproved}
      setPreviewApproved={setPreviewApproved}
      notificationEmails={notificationEmails}
      setNotificationEmails={setNotificationEmails}
      setTenants={setTenants}
      getAccessToken={getAccessToken}
      setError={setError}
      setSuccessMessage={setSuccessMessage}
    />
  );
}
