"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import OffboardingSection from "../../components/OffboardingSection";

export function SectionOffboarding() {
  const {
    showOffboardDialog, setShowOffboardDialog,
    offboardConfirmText, setOffboardConfirmText,
    offboarding, offboardError, setOffboardError,
    handleOffboard,
    offboardingInProgress, handleDrainBarrierElapsed,
  } = useTenantConfig();

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
      />
    </>
  );
}
