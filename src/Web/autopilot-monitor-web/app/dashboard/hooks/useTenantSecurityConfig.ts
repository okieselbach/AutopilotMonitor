"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { TokenExpiredError } from "@/lib/authenticatedFetch";
import { dedupedAuthFetch } from "@/lib/dedupedAuthFetch";
import type { NotificationType } from "@/contexts/NotificationContext";

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

interface TenantConfigurationSummary {
  validateAutopilotDevice: boolean;
}

interface User {
  isTenantAdmin?: boolean;
  isGlobalAdmin?: boolean;
  role?: string | null;
}

/**
 * Fetches the tenant's security config (validateAutopilotDevice flag) to drive the
 * "Autopilot Device Validation is disabled" banner on the dashboard.
 *
 * Returns null while loading, true/false once resolved, or null on error.
 * Skips the fetch for regular users (they never see the dashboard).
 */
export function useTenantSecurityConfig(
  tenantId: string | null | undefined,
  user: User | null | undefined,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: AddNotification,
): boolean | null {
  const [serialValidationEnabled, setSerialValidationEnabled] = useState<boolean | null>(null);

  useEffect(() => {
    // Wait until both tenant and user are resolved. Without the !user guard
    // the effect fires once with user=null (initial render before AuthContext
    // settles, all user.* deps "undefined") and once with user=UserInfo
    // (deps now booleans/role string) — two distinct dep tuples, two
    // fetches. Holding off until user is non-null collapses to one fetch.
    if (!tenantId || !user) return;
    const fetchTenantSecurityConfig = async () => {
      // Skip for users without a tenant role — they'd just 401/403 anyway.
      if (!user.isTenantAdmin && !user.isGlobalAdmin && user.role == null) return;

      try {
        const response = await dedupedAuthFetch(api.config.featureFlags(tenantId), getAccessToken);

        if (!response.ok) {
          setSerialValidationEnabled(null);
          return;
        }

        const data: TenantConfigurationSummary = await response.json();
        setSerialValidationEnabled(!!data.validateAutopilotDevice);
      } catch (error) {
        if (error instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        }
        setSerialValidationEnabled(null);
      }
    };

    fetchTenantSecurityConfig();
    // Depend on the user fields actually read above (primitives), not the
    // whole user object — its identity flips when AuthContext swaps the
    // prefetched user object for the freshly fetched one, which would
    // otherwise refire this effect and produce a duplicate feature-flags
    // request.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId, user?.isTenantAdmin, user?.isGlobalAdmin, user?.role]);

  return serialValidationEnabled;
}
