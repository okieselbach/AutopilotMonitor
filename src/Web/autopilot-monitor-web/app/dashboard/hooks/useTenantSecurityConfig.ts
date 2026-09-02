"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { TokenExpiredError } from "@/lib/authenticatedFetch";
import { dedupedAuthFetch } from "@/lib/dedupedAuthFetch";
import type { NotificationType } from "@/contexts/NotificationContext";
import { missingContactProfileParts } from "@/lib/edition";

type AddNotification = (
  type: NotificationType,
  title: string,
  message: string,
  key?: string,
  href?: string,
) => void;

/** The subset of the feature-flags response the dashboard banners read. */
export interface TenantConfigurationSummary {
  validateAutopilotDevice: boolean;
  edition?: string;
  contactEmailSet?: boolean;
  companyNameSet?: boolean;
  appHomingFunnelActive?: boolean;
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
   * Pro tenant (incl. trial) with an incomplete contact profile (address and/or company
   * name) — drives the amber "complete your contact details" banner. Requires an EXPLICIT
   * false flag from the backend, so loading/error/older-backend states never nag.
   */
  proContactMissing: boolean;
  /** Display labels of the missing parts ("contact address", "company name"); empty when complete. */
  proContactMissingParts: string[];
  /**
   * Dual app-reg window: the tenant still runs on the previous app registration and the
   * self-service switch is open — drives the blue "switch to the new app" banner. Requires an
   * EXPLICIT true from the backend; loading/error/older-backend states show nothing.
   */
  appHomingFunnelActive: boolean;
}

const EMPTY_SUMMARY: TenantSecuritySummary = {
  serialValidationEnabled: null,
  proContactMissing: false,
  proContactMissingParts: [],
  appHomingFunnelActive: false,
};

/** Pure projection of a feature-flags body onto the banner summary (unit-tested). */
export function summarizeTenantSecurityConfig(data: TenantConfigurationSummary): TenantSecuritySummary {
  const isPro = data.edition === "pro" || data.edition === "enterprise";
  const missingParts = isPro ? missingContactProfileParts(data) : [];
  return {
    serialValidationEnabled: !!data.validateAutopilotDevice,
    proContactMissing: missingParts.length > 0,
    proContactMissingParts: missingParts,
    appHomingFunnelActive: data.appHomingFunnelActive === true,
  };
}

/**
 * Fetches the tenant's feature-flags summary to drive the dashboard banners:
 * the red "Autopilot Device Validation is disabled" banner, the amber
 * Pro-without-contact-address banner, and the blue app-registration migration banner.
 *
 * Skips the fetch for users without a tenant role (they never see the dashboard).
 */
export function useTenantSecurityConfig(
  tenantId: string | null | undefined,
  user: User | null | undefined,
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: AddNotification,
): TenantSecuritySummary {
  const [summary, setSummary] = useState<TenantSecuritySummary>(EMPTY_SUMMARY);

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
          setSummary(EMPTY_SUMMARY);
          return;
        }

        const data: TenantConfigurationSummary = await response.json();
        setSummary(summarizeTenantSecurityConfig(data));
      } catch (error) {
        if (error instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        }
        setSummary(EMPTY_SUMMARY);
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
