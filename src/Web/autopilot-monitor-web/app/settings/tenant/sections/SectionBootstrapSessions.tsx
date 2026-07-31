"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import BootstrapSessionsSection from "../../components/BootstrapSessionsSection";

export function SectionBootstrapSessions() {
  const {
    config, editionInfo,
    bootstrapSessions, bootstrapLoading,
    fetchBootstrapSessions, revokeBootstrapSession, createBootstrapSession,
  } = useTenantConfig();

  // Effective availability: included in the Pro plan, or enabled per tenant via the GA flag
  // (mirrors the backend's TenantEntitlementService.IsBootstrapEnabled).
  if (editionInfo.edition !== "pro" && !config?.bootstrapTokenEnabled) {
    return (
      <div className="bg-white rounded-lg shadow p-8 text-center">
        <p className="text-gray-500">Bootstrap Sessions are not available for this tenant.</p>
      </div>
    );
  }

  return (
    <>
      <TenantNotifications />
      <BootstrapSessionsSection
        sessions={bootstrapSessions}
        loading={bootstrapLoading}
        onRefresh={fetchBootstrapSessions}
        onRevoke={revokeBootstrapSession}
        onCreate={createBootstrapSession}
      />
    </>
  );
}
