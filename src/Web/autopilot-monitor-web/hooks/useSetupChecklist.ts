"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/contexts/AuthContext";
import { useTenant } from "@/contexts/TenantContext";
import { api } from "@/lib/api";
import { trackEvent } from "@/lib/appInsights";
import { cachedAuthFetchJson, FEATURE_FLAGS_TTL_MS } from "@/lib/cachedAuthFetch";
import {
  deriveSetupChecklist,
  SETUP_TOUR_TARGETS,
  type SetupChecklist,
  type SetupGuideFlags,
  type SetupGuideLocalMarks,
  type SetupStepId,
} from "@/lib/setupGuide";
import { closeSetupGuide, openSetupGuide, readSetupMarks, startSetupTour, useSetupRevision } from "@/lib/setupGuideStore";

export interface SetupChecklistState {
  checklist: SetupChecklist | null;
  /** The flags of this tenant are still loading; a failed load ends it too (the checklist stays null). */
  pending: boolean;
  tenantId: string;
  /** A tenant admin or Global Admin of this tenant, outside view-as: may finish steps. */
  canEdit: boolean;
  /** Why the steps are read-only here; null when canEdit. */
  readOnlyReason: "view-as" | "role" | null;
}

interface Loaded {
  tenantId: string;
  /** null: the load failed and the guide stays hidden. */
  flags: SetupGuideFlags | null;
  marks: SetupGuideLocalMarks;
}

/**
 * The setup checklist of the signed-in user's own tenant, from the (cached) feature flags plus
 * what this browser just did. Members without a tenant role get nothing.
 */
export function useSetupChecklist(): SetupChecklistState {
  const { user, getAccessToken } = useAuth();
  const { tenantId } = useTenant();
  const revision = useSetupRevision();
  const [loaded, setLoaded] = useState<Loaded | null>(null);

  const isAdmin = !!(user?.isTenantAdmin || user?.isGlobalAdmin);
  const hasTenantRole = isAdmin || user?.role != null;
  const viewAs = !!user?.viewAs;

  useEffect(() => {
    if (!tenantId || !hasTenantRole) return;
    let cancelled = false;
    const run = async () => {
      try {
        const flags = await cachedAuthFetchJson<SetupGuideFlags>(
          api.config.featureFlags(tenantId), getAccessToken, { ttlMs: FEATURE_FLAGS_TTL_MS });
        if (!cancelled) setLoaded({ tenantId, flags, marks: readSetupMarks(tenantId) });
      } catch {
        // Fail-soft: without flags the guide stays hidden and the dashboard falls back to its banners.
        if (!cancelled) setLoaded({ tenantId, flags: null, marks: readSetupMarks(tenantId) });
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [tenantId, hasTenantRole, revision, getAccessToken]);

  const current = loaded && loaded.tenantId === tenantId && hasTenantRole ? loaded : null;
  return {
    checklist: current?.flags ? deriveSetupChecklist(current.flags, current.marks) : null,
    pending: hasTenantRole && !!tenantId && current === null,
    tenantId,
    canEdit: isAdmin && !viewAs,
    readOnlyReason: viewAs ? "view-as" : isAdmin ? null : "role",
  };
}

/**
 * Continues the guide at `step`: the validation step runs as a spotlight on the real settings page
 * (its consent redirect comes back there), every other step opens in the dialog.
 */
export function useContinueSetup(): (step: SetupStepId | null, source: string) => void {
  const router = useRouter();
  return useCallback((step: SetupStepId | null, source: string) => {
    if (step === "validation") {
      trackEvent("setup_guide_opened", { source, step });
      closeSetupGuide();
      startSetupTour("validation");
      router.push(SETUP_TOUR_TARGETS.validation.path);
      return;
    }
    openSetupGuide(source, step);
  }, [router]);
}
