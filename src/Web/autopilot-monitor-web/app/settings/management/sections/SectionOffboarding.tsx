"use client";

import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import OffboardingSection from "../../components/OffboardingSection";

export function SectionOffboarding() {
  const {
    canEditConfig,
    showOffboardDialog, setShowOffboardDialog,
    offboardConfirmText, setOffboardConfirmText,
    offboarding, offboardError, setOffboardError,
    handleOffboard,
    offboardingInProgress, handleDrainBarrierElapsed,
  } = useTenantConfig();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  // Offboarding deletes the whole tenant — Operators never see the danger zone.
  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
        This page is available to tenant administrators only.
      </div>
    );
  }

  return (
    <>
      <TenantNotifications />
      <OffboardingSection
        showOffboardDialog={showOffboardDialog}
        setShowOffboardDialog={setShowOffboardDialog}
        offboardConfirmText={offboardConfirmText}
        setOffboardConfirmText={setOffboardConfirmText}
        offboarding={offboarding}
        offboardError={offboardError}
        setOffboardError={setOffboardError}
        onOffboard={handleOffboard}
        offboardingInProgress={offboardingInProgress}
        onDrainBarrierElapsed={handleDrainBarrierElapsed}
        tenantId={tenantId}
        getAccessToken={getAccessToken}
      />
    </>
  );
}
