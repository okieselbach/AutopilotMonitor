"use client";

import { Suspense, useEffect, useRef } from "react";
import { useSearchParams } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { useContinueSetup } from "@/hooks/useSetupChecklist";
import { parseSetupStep } from "@/lib/setupGuide";
import { useSetupGuideDialog } from "@/lib/setupGuideStore";
import { SetupGuideModal } from "./SetupGuideModal";

/**
 * Mounts the setup guide dialog once, in the root layout; the dashboard card, the sidebar entry,
 * the help menu and the settings tour open it through the module store. Unmounted while closed,
 * so every opening starts at the requested (or first open) step.
 *
 * `?setup-guide=settings|hardware|validation|deploy` opens it at that step, also for a tenant
 * that finished the setup: nothing is reset, the steps show the tenant's real configuration.
 */
export function SetupGuideHost() {
  const { open, step } = useSetupGuideDialog();
  return (
    <>
      {/* useSearchParams() needs a Suspense boundary for the static prerender. */}
      <Suspense fallback={null}>
        <SetupGuideDeepLink />
      </Suspense>
      {open && <SetupGuideModal initialStep={step} />}
    </>
  );
}

/**
 * Opens the guide once each time the parameter arrives, not only on the first mount: a link
 * opened without a session goes through the sign-in and reaches the page again by a client-side
 * navigation, where this layout stays mounted. Admins only, like the help menu entry.
 */
function SetupGuideDeepLink() {
  const requested = parseSetupStep(useSearchParams()?.get("setup-guide"));
  const { user, isAuthenticated, isLoading } = useAuth();
  const continueSetup = useContinueSetup();
  const allowed = isAuthenticated && !isLoading && !!(user?.isTenantAdmin || user?.isGlobalAdmin);
  const handled = useRef(false);

  useEffect(() => {
    if (requested === null) {
      handled.current = false;
      return;
    }
    if (handled.current || !allowed) return;
    handled.current = true;
    continueSetup(requested, "deeplink");
  }, [requested, allowed, continueSetup]);

  return null;
}
