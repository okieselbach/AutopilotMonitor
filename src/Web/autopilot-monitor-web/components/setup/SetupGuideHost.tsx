"use client";

import { useSetupGuideDialog } from "@/lib/setupGuideStore";
import { SetupGuideModal } from "./SetupGuideModal";

/**
 * Mounts the setup guide dialog once, in the root layout; the dashboard card, the sidebar entry,
 * the help menu and the settings tour open it through the module store. Unmounted while closed,
 * so every opening starts at the requested (or first open) step.
 */
export function SetupGuideHost() {
  const { open, step } = useSetupGuideDialog();
  if (!open) return null;
  return <SetupGuideModal initialStep={step} />;
}
