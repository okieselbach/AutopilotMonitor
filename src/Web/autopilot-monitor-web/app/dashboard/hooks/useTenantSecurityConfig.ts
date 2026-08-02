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
  edition?: string;
  contactEmailSet?: boolean;
}

interface User {
  isTenantAdmin?: boolean;
  isGlobalAdmin?: boolean;
  role?: string | null;
}

export interface TenantSecuritySummary {
  /** null while loading or on error; the red banner keys on `=== false`. */
  serialValidationEnabled: boolean | null;
  /**
   * Pro tenant (incl. trial) with no stored contact address — drives the amber
   * "set a contact address" banner. Requires an EXPLICIT contactEmailSet:false from
   * the backend, so loading/error/older-backend states never nag.
   */
  proContactMissing: boolean;
}

/**
 * Fetches the tenant's feature-flags summary to drive the dashboard banners:
 * the red "Autopilot Device Validation is disabled" banner and the amber
 * Pro-without-contact-address banner.
 *
 * Skips the fetch for regular users (they never see the dashboard).
 */
export function useTenantSecurityConfig(
  tenantId: string | null | undefined,
  user: User | null | undefined,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: AddNotification,
): TenantSecuritySummary {
  const [summary, setSummary] = useState<TenantSecuritySummary>({
    serialValidationEnabled: null,
    proContactMissing: false,
  });

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
          setSummary({ serialValidationEnabled: null, proContactMissing: false });
          return;
        }

        const data: TenantConfigurationSummary = await response.json();
        setSummary({
          serialValidationEnabled: !!data.validateAutopilotDevice,
          proContactMissing:
            (data.edition === "pro" || data.edition === "enterprise") &&
            data.contactEmailSet === false,
        });
      } catch (error) {
        if (error instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        }
        setSummary({ serialValidationEnabled: null, proContactMissing: false });
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

  return summary;
}
