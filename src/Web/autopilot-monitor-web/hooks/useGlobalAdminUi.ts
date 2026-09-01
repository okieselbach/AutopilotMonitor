"use client";

import { useAuth } from "@/contexts/AuthContext";
import { useAdminMode } from "./useAdminMode";

/**
 * True when Global-Admin-EXCLUSIVE UI should be visible: the caller is a real Global Admin AND the
 * Global-Admin view is switched on (which demo mode forces off — see lib/demoMode.ts).
 *
 * Use this for operator-only surfaces that sit INSIDE an otherwise tenant-scoped page — the
 * platform-bot Telegram provider, the cert-device-binding toggle, the retention escape hatch, the
 * backend/portal build blocks. Turning the Global-Admin view off then yields a view that is
 * genuinely indistinguishable from a tenant admin's, which is what live demos need.
 *
 * NOT a permission check. The server enforces every one of these rules on its own
 * (TenantConfigValidation, GlobalAdminOnly → 403), and {@link useCanMutatePlatform} stays bound to
 * the identity alone — it gates the mutating controls in the cross-tenant /admin area, where a
 * view toggle must never decide what may be written.
 */
export function useGlobalAdminUi(): boolean {
  const { user } = useAuth();
  const { globalAdminMode } = useAdminMode();
  return user?.isGlobalAdmin === true && globalAdminMode;
}
