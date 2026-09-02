"use client";

import { Suspense, useState, useEffect } from "react";
import type { DelegatedSlotUsageResponse } from "@/utils/wire-types.generated";
import { useSearchParams } from "next/navigation";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { classifyClientId, legacyConfigured } from "@/lib/authApp";
import { appHomingErrorMessage } from "@/lib/appHoming";
import { trackEvent } from "@/lib/appInsights";
import { TenantAdminSection } from "./TenantAdminSection";
import { IdentityBindingsSection } from "./IdentityBindingsSection";
import { AppHomingConfirmDialog } from "./AppHomingConfirmDialog";
import { OffboardTenantConfirmDialog } from "./OffboardTenantConfirmDialog";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";
import { matchesTenantSearch, notificationEmailFor, parseTenantSearch } from "./tenantSearch";

export interface TenantConfiguration {
  tenantId: string;
  domainName: string;
  lastUpdated: string;
  updatedBy: string;
  /** Tenant-maintained contact address for service matters. Read-only here. */
  contactEmail?: string | null;
  /** Tenant-maintained company name (contact profile). Read-only here. */
  companyName?: string | null;
  disabled: boolean;
  disabledReason?: string;
  disabledUntil?: string;
  /** Per-tenant device API rate-limit override (null/undefined = inherit global). GA-only. */
  customRateLimitRequestsPerMinute?: number | null;
  /** Per-tenant user API rate-limit override for standard users (null/undefined = inherit global). GA-only. */
  customUserRateLimitRequestsPerMinute?: number | null;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateAutopilotDevice: boolean;
  allowInsecureAgentRequests?: boolean;
  bootstrapTokenEnabled?: boolean;
  unrestrictedModeEnabled?: boolean;
  entraAppRolesEnabled?: boolean;
  /** Operator-set (GA-only): Device-phase ESP failures on Continue-Anyway profiles get a 60-min observation instead of an immediate hard fail. */
  enableEspContinueAnywayObservation?: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  planTier?: string;
  /** Pro-trial end (ISO, UTC). Null/undefined = no trial. Managed via PATCH plan. */
  trialExpiresUtc?: string | null;
  /** Whether the tenant has used its one self-service trial. */
  trialConsumed?: boolean;
  /** Delegated (MSP) tenant slot override; null/undefined = plan entitlement (Community 0, Pro 2). Managed via PATCH plan. */
  maxDelegatedTenantsOverride?: number | null;
  /**
   * Dual app-reg homing: null/undefined = legacy app. Typed explicitly so a payload refactor
   * cannot silently drop the field on the generic PUT round-trip (absent ⇒ backend resets to
   * legacy). Mutated ONLY via POST app-homing — the PUT preserves it server-side.
   */
  homedAppClientId?: string | null;
  /** System-written login provenance (AuthFunction) — read-only observability. */
  lastAuthClientId?: string | null;
  lastAuthClientIdSince?: string | null;
}

/** Small "New app" / "Legacy app" indicator for the dual app-reg parallel window. */
function HomingBadge({ clientId }: { clientId?: string | null }) {
  const kind = classifyClientId(clientId);
  const styles =
    kind === "primary" ? "bg-sky-100 text-sky-800"
    : kind === "legacy" ? "bg-gray-100 text-gray-600"
    : "bg-amber-100 text-amber-800";
  const label = kind === "primary" ? "New app" : kind === "legacy" ? "Legacy app" : "Unknown app";
  return (
    <span className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${styles}`}>
      {label}
    </span>
  );
}

export interface TenantManagementSectionProps {
  tenants: TenantConfiguration[];
  loadingTenants: boolean;
  fetchTenants: () => void;
  previewApproved: Set<string>;
  setPreviewApproved: React.Dispatch<React.SetStateAction<Set<string>>>;
  /** Welcome-mail addresses keyed by lowercased tenant id (see AdminConfigContext). */
  notificationEmails: Record<string, string>;
  setNotificationEmails: React.Dispatch<React.SetStateAction<Record<string, string>>>;
  setTenants: React.Dispatch<React.SetStateAction<TenantConfiguration[]>>;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function TenantManagementSection(props: TenantManagementSectionProps) {
  // useSearchParams() in the inner component requires a Suspense boundary for
  // static prerender (this section renders inside the prerendered
  // /admin/tenants/management page).
  return (
    <Suspense fallback={null}>
      <TenantManagementSectionInner {...props} />
    </Suspense>
  );
}

function TenantManagementSectionInner({
  tenants,
  loadingTenants,
  fetchTenants,
  previewApproved,
  setPreviewApproved,
  notificationEmails,
  setNotificationEmails,
  setTenants,
  getAccessToken,
  setError,
  setSuccessMessage,
}: TenantManagementSectionProps) {
  // Read-only Global Readers may view tenants (incl. config report) but not edit them.
  const canMutate = useCanMutatePlatform();
  // Deep link from GA notifications: ?tenantId=… seeds the search box once, so the
  // list opens filtered to the tenant the notification is about. Seeded via the
  // useState initializer when the param is present at mount; the adjust-during-render
  // block below covers a param that only appears on a later soft navigation.
  const searchParams = useSearchParams();
  const tenantIdParam = searchParams?.get("tenantId") ?? null;
  const [searchQuery, setSearchQuery] = useState(() => tenantIdParam ?? "");
  const [seededSearch, setSeededSearch] = useState(tenantIdParam !== null);
  if (!seededSearch && tenantIdParam) {
    setSeededSearch(true);
    setSearchQuery(tenantIdParam);
  }
  // Mount-time clock for trial-expiry checks — render must stay pure
  // (react-hooks/purity); day-granularity expiry doesn't need a live clock.
  const [nowMs] = useState(() => Date.now());
  const [showOnlyWaitlist, setShowOnlyWaitlist] = useState(false);
  const [showOnlyReady, setShowOnlyReady] = useState(false);
  const [tenantSectionExpanded, setTenantSectionExpanded] = useState(false);
  const [editingTenant, setEditingTenant] = useState<TenantConfiguration | null>(null);
  const [savingTenant, setSavingTenant] = useState(false);
  const [savingPlan, setSavingPlan] = useState(false);
  // Delegated (MSP) slot usage of the tenant being edited (GET global/delegated-slots/{id}); null = not loaded.
  const [slotUsage, setSlotUsage] = useState<DelegatedSlotUsageResponse | null>(null);
  const [releasingHold, setReleasingHold] = useState<string | null>(null);
  // Support escape hatch: end a removed customer's 24 h slot hold early (GlobalAdminOnly, audited).
  const handleReleaseHold = async (tenantId: string, invitationId: string) => {
    if (!canMutate) return;
    try {
      setReleasingHold(invitationId);
      setError(null);
      const response = await authenticatedFetch(api.delegatedSlots.releaseHold(tenantId), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ invitationId }),
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data.error || `Failed to release the hold: ${response.statusText}`);
      }
      setSlotUsage((prev) => prev
        ? { ...prev, holds: prev.holds.filter((h) => h.invitationId !== invitationId), used: Math.max(0, prev.used - 1) }
        : prev);
      setSuccessMessage("Slot hold released — the managing tenant can invite again now.");
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to release the hold");
    } finally {
      setReleasingHold(null);
    }
  };
  const editingTenantId = editingTenant?.tenantId ?? null;
  useEffect(() => {
    if (!editingTenantId) return;
    let cancelled = false;
    authenticatedFetch(api.delegatedSlots.get(editingTenantId), getAccessToken)
      .then(async (res) => {
        if (cancelled) return;
        setSlotUsage(res.ok ? ((await res.json()) as DelegatedSlotUsageResponse) : null);
      })
      .catch(() => {
        if (!cancelled) setSlotUsage(null);
      });
    return () => {
      cancelled = true;
    };
  }, [editingTenantId, getAccessToken]);
  const [homingDialogTarget, setHomingDialogTarget] = useState<"primary" | "legacy" | null>(null);
  const [savingHoming, setSavingHoming] = useState(false);
  // Failures of the homing/offboard calls go into these instead of the page-level
  // error banner — that banner sits behind the editor modal (z-50) and the confirm
  // dialogs (z-[60]), so the user would never see the feedback.
  const [homingError, setHomingError] = useState<string | null>(null);
  const [offboardDialogOpen, setOffboardDialogOpen] = useState(false);
  const [offboarding, setOffboarding] = useState(false);
  const [offboardError, setOffboardError] = useState<string | null>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const tenantsPerPage = tenantSectionExpanded ? 7 : 3;

  // Preview Whitelist state
  const [togglingPreviewTenant, setTogglingPreviewTenant] = useState<string | null>(null);
  const [sendingWelcomeEmail, setSendingWelcomeEmail] = useState(false);
  const [notificationEmail, setNotificationEmail] = useState("");

  // Filter and sort tenants. Matching (incl. the quoted exact mode and the two
  // searchable addresses) lives in tenantSearch.ts.
  const searchTerm = parseTenantSearch(searchQuery);
  const filteredTenants = tenants.filter(t => {
    const matchesSearch = matchesTenantSearch(
      t, searchTerm, notificationEmailFor(notificationEmails, t.tenantId));
    const matchesWaitlist = !showOnlyWaitlist || !previewApproved.has(t.tenantId);
    const matchesReady = !showOnlyReady || t.validateAutopilotDevice;
    return matchesSearch && matchesWaitlist && matchesReady;
  });

  // Statistics (always over all tenants, not filtered)
  const readyCount = tenants.filter(t => t.validateAutopilotDevice).length;
  const waitlistCount = tenants.filter(t => !previewApproved.has(t.tenantId)).length;
  const totalCount = tenants.length;

  // Pagination
  const totalPages = Math.ceil(filteredTenants.length / tenantsPerPage);
  const startIndex = currentPage * tenantsPerPage;
  const endIndex = startIndex + tenantsPerPage;
  const paginatedTenants = filteredTenants.slice(startIndex, endIndex);

  // Reset to first page when the search or a filter changes (adjust-during-render
  // pattern, see react.dev "storing information from previous renders").
  const [prevFilterKey, setPrevFilterKey] = useState<[string, boolean, boolean]>([searchQuery, showOnlyWaitlist, showOnlyReady]);
  if (prevFilterKey[0] !== searchQuery || prevFilterKey[1] !== showOnlyWaitlist || prevFilterKey[2] !== showOnlyReady) {
    setPrevFilterKey([searchQuery, showOnlyWaitlist, showOnlyReady]);
    setCurrentPage(0);
  }

  const handleSaveTenant = async (tenant: TenantConfiguration) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setSavingTenant(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.config.tenant(tenant.tenantId), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(tenant),
      });

      if (!response.ok) {
        throw new Error(`Failed to save tenant configuration: ${response.statusText}`);
      }

      const result = await response.json();

      // Update tenant in list
      setTenants(prev => prev.map(t => t.tenantId === tenant.tenantId ? result.config : t));
      setEditingTenant(null);
      setSuccessMessage(`Tenant ${tenant.tenantId} configuration saved successfully!`);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving tenant configuration");
      } else {
        console.error("Error saving tenant configuration:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save tenant configuration");
    } finally {
      setSavingTenant(false);
    }
  };

  // Plan & trial have their OWN save path (PATCH /config/{id}/plan) — the generic PUT above
  // preserves these fields server-side, so they can only be mutated here.
  const handleSavePlan = async (tenant: TenantConfiguration) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setSavingPlan(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.config.plan(tenant.tenantId), getAccessToken, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          // The select only offers the two canonical write values (community/pro); legacy stored
          // "enterprise" normalizes to "pro" on save, legacy "free" to "community".
          planTier: tenant.planTier === "pro" || tenant.planTier === "enterprise" ? "pro" : "community",
          trialExpiresUtc: tenant.trialExpiresUtc ?? null,
          maxDelegatedTenants: tenant.maxDelegatedTenantsOverride ?? null,
        }),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to save plan: ${response.statusText}`);
      }

      const result = await response.json();
      const apply = (t: TenantConfiguration): TenantConfiguration => ({
        ...t,
        planTier: result.planTier,
        trialExpiresUtc: result.trialExpiresUtc ?? null,
        trialConsumed: result.trialConsumed ?? t.trialConsumed,
        maxDelegatedTenantsOverride: result.maxDelegatedTenantsOverride ?? null,
      });
      setSlotUsage((prev) => (prev ? { ...prev, limit: result.maxDelegatedTenants ?? prev.limit, overrideLimit: result.maxDelegatedTenantsOverride ?? undefined } : prev));
      setTenants(prev => prev.map(t => (t.tenantId === tenant.tenantId ? apply(t) : t)));
      setEditingTenant(prev => (prev && prev.tenantId === tenant.tenantId ? apply(prev) : prev));
      setSuccessMessage(`Plan saved — effective edition: ${result.effectiveEdition}`);
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving plan");
      }
      setError(err instanceof Error ? err.message : "Failed to save plan");
    } finally {
      setSavingPlan(false);
    }
  };

  // App-reg homing has its OWN save path (POST app-homing) — like plan/trial, the generic PUT
  // preserves the field server-side, so it can only be mutated via the confirm dialog here.
  const handleFlipHoming = async (tenant: TenantConfiguration, target: "primary" | "legacy", force: boolean) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setSavingHoming(true);
      setHomingError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.config.appHoming(tenant.tenantId), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ target, force }),
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        trackEvent("app_homing_manual_flip_failed", {
          tenantId: tenant.tenantId,
          target,
          force,
          reason: data.reason ?? `http-${response.status}`,
        });
        throw new Error(appHomingErrorMessage(data.reason, response.statusText));
      }
      trackEvent("app_homing_manual_flip", {
        tenantId: tenant.tenantId,
        target,
        force,
        changed: data.changed === true,
      });

      const apply = (t: TenantConfiguration): TenantConfiguration => ({
        ...t,
        homedAppClientId: data.homedAppClientId ?? null,
        lastAuthClientId: data.lastAuthClientId ?? t.lastAuthClientId,
        lastAuthClientIdSince: data.lastAuthClientIdSince ?? t.lastAuthClientIdSince,
      });
      setTenants(prev => prev.map(t => (t.tenantId === tenant.tenantId ? apply(t) : t)));
      setEditingTenant(prev => (prev && prev.tenantId === tenant.tenantId ? apply(prev) : prev));
      setHomingDialogTarget(null);
      setSuccessMessage(
        data.changed
          ? `Tenant ${tenant.tenantId} now uses the ${target === "primary" ? "new" : "legacy"} app registration`
          : `Tenant ${tenant.tenantId} was already on the ${target === "primary" ? "new" : "legacy"} app registration`
      );
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while switching app homing");
      }
      setHomingError(err instanceof Error ? err.message : "Failed to switch app registration");
    } finally {
      setSavingHoming(false);
    }
  };

  // Offboarding has its OWN path (DELETE tenants/{id}/offboard) — a queued cascade, not part
  // of the modal's Save. The backend suspends the tenant immediately (Disabled-gate) and
  // deletes all data after the drain barrier; the endpoint is idempotent on re-click.
  const handleOffboardTenant = async (tenant: TenantConfiguration) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setOffboarding(true);
      setOffboardError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.tenants.offboard(tenant.tenantId), getAccessToken, {
        method: "DELETE",
      });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) {
        trackEvent("admin_tenant_offboard_failed", {
          tenantId: tenant.tenantId,
          reason: data.error ?? `http-${response.status}`,
        });
        throw new Error(data.error || `Failed to offboard tenant: ${response.statusText}`);
      }
      trackEvent("admin_tenant_offboarded", {
        tenantId: tenant.tenantId,
        status: data.status ?? "unknown",
      });

      setOffboardDialogOpen(false);
      setEditingTenant(null);
      // The tenant now shows as suspended ("Offboarding in progress") until the cascade
      // removes its row entirely — refresh instead of patching local state.
      fetchTenants();
      setSuccessMessage(data.message || `Offboarding queued for tenant ${tenant.tenantId}`);
      setTimeout(() => setSuccessMessage(null), 8000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while offboarding tenant");
      } else {
        console.error("Error offboarding tenant:", err);
      }
      setOffboardError(err instanceof Error ? err.message : "Failed to offboard tenant");
    } finally {
      setOffboarding(false);
    }
  };

  const handleSendWelcomeEmail = async (tenantId: string, email?: string) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setSendingWelcomeEmail(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(
        api.preview.sendWelcomeEmail(tenantId),
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ email: email || "" }),
        }
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to send welcome email: ${response.statusText}`);
      }

      const result = await response.json();
      // The send persisted the address too — keep the search map in step so the tenant is
      // findable by it right away instead of only after the next list refresh.
      if (result.email) {
        setNotificationEmails(prev => ({ ...prev, [tenantId.toLowerCase()]: result.email }));
      }
      setSuccessMessage(`Welcome email sent to ${result.email}`);
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while sending welcome email");
      }
      setError(err instanceof Error ? err.message : "Failed to send welcome email");
    } finally {
      setSendingWelcomeEmail(false);
    }
  };

  const handleTogglePreview = async (tenantId: string) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setTogglingPreviewTenant(tenantId);
      setError(null);
      setSuccessMessage(null);

      const isCurrentlyApproved = previewApproved.has(tenantId);

      const response = await authenticatedFetch(api.preview.whitelistTenant(tenantId), getAccessToken, {
        method: isCurrentlyApproved ? "DELETE" : "POST",
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to update preview access: ${response.statusText}`);
      }

      setPreviewApproved(prev => {
        const next = new Set(prev);
        if (isCurrentlyApproved) {
          next.delete(tenantId);
        } else {
          next.add(tenantId);
        }
        return next;
      });

      setSuccessMessage(
        isCurrentlyApproved
          ? `Preview access revoked for tenant ${tenantId}`
          : `Preview access granted for tenant ${tenantId}`
      );
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while updating preview access");
      }
      setError(err instanceof Error ? err.message : "Failed to update preview access");
    } finally {
      setTogglingPreviewTenant(null);
    }
  };

  return (
    <>
      <div className="bg-gradient-to-br from-green-50 to-emerald-50 border-2 border-green-300 rounded-lg shadow-lg">
        <div
          className="p-6 border-b border-green-200 bg-gradient-to-r from-green-100 to-emerald-100 cursor-pointer select-none"
          onClick={() => { setTenantSectionExpanded(v => !v); setCurrentPage(0); }}
        >
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-2">
              <svg className="w-6 h-6 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16m14 0h2m-2 0h-5m-9 0H3m2 0h5M9 7h1m-1 4h1m4-4h1m-1 4h1m-5 10v-5a1 1 0 011-1h2a1 1 0 011 1v5m-4 0h4" />
              </svg>
              <div>
                <h2 className="text-xl font-semibold text-green-900">Tenant Management</h2>
                <p className="text-sm text-green-600 mt-1">View and manage all tenant configurations</p>
              </div>
            </div>
            <div className="flex items-center space-x-2">
              <button
                onClick={(e) => { e.stopPropagation(); fetchTenants(); }}
                disabled={loadingTenants}
                className="p-1.5 rounded-lg text-green-700 hover:bg-green-200 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                title="Refresh tenants"
              >
                <svg className={`w-4 h-4 ${loadingTenants ? 'animate-spin' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                </svg>
              </button>
              <svg
                className={`w-5 h-5 text-green-700 transition-transform duration-200 ${tenantSectionExpanded ? 'rotate-180' : ''}`}
                fill="none" stroke="currentColor" viewBox="0 0 24 24"
              >
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
              </svg>
            </div>
          </div>
        </div>
        <div className="p-6">
          {loadingTenants ? (
            <div className="text-center py-8">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-green-600 mx-auto"></div>
              <p className="mt-3 text-gray-600 text-sm">Loading tenants...</p>
            </div>
          ) : (
            <div className="space-y-4">
              {/* Search */}
              <div className="flex items-center justify-between space-x-2 mb-4">
                <div className="relative flex-1">
                  <svg className="absolute left-3 top-1/2 transform -translate-y-1/2 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                  </svg>
                  <input
                    type="text"
                    placeholder={'Search by domain, tenant ID or email (use "..." for exact match)'}
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    className="w-full pl-10 pr-10 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 transition-colors"
                  />
                  {searchQuery && (
                    <button
                      onClick={() => setSearchQuery('')}
                      className="absolute right-3 top-1/2 transform -translate-y-1/2 text-gray-400 hover:text-gray-600 transition-colors"
                      aria-label="Clear search"
                    >
                      <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                      </svg>
                    </button>
                  )}
                </div>
                <button
                  onClick={() => { setShowOnlyReady(v => !v); setCurrentPage(0); }}
                  className={`flex items-center space-x-1 px-3 py-2 text-sm rounded-lg border transition-colors whitespace-nowrap ${
                    showOnlyReady
                      ? 'bg-green-600 text-white border-blue-600'
                      : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
                  }`}
                >
                  {showOnlyReady && (
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                    </svg>
                  )}
                  <span>Ready</span>
                </button>
                <button
                  onClick={() => { setShowOnlyWaitlist(v => !v); setCurrentPage(0); }}
                  className={`flex items-center space-x-1 px-3 py-2 text-sm rounded-lg border transition-colors whitespace-nowrap ${
                    showOnlyWaitlist
                      ? 'bg-amber-500 text-white border-amber-500'
                      : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
                  }`}
                >
                  {showOnlyWaitlist && (
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                    </svg>
                  )}
                  <span>Waitlist</span>
                </button>
              </div>

              {/* Tenant List */}
              <div className="space-y-3">
                {paginatedTenants.length === 0 ? (
                  <div className="text-center py-8 text-gray-500">
                    {showOnlyWaitlist ? "No waitlist tenants found" : searchQuery ? "No tenants found matching your search" : "No tenants registered yet"}
                  </div>
                ) : (
                  <>
                    {paginatedTenants.map((tenant) => (
                      <div
                        key={tenant.tenantId}
                        className={`border rounded-lg p-4 transition-all ${
                          tenant.disabled
                            ? 'bg-red-50 border-red-300'
                            : 'bg-white border-gray-200 hover:border-green-300'
                        }`}
                      >
                        <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-2">
                          <div>
                            <h3 className="font-semibold text-gray-900 text-lg">
                              {tenant.domainName || tenant.tenantId}
                            </h3>
                            <p className="text-sm text-gray-500 mt-0.5">
                              Tenant ID: {tenant.tenantId}
                            </p>
                          </div>
                          <div className="flex flex-wrap items-center gap-2">
                            <div className="flex flex-wrap items-center gap-2">
                              {tenant.disabled && (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-red-100 text-red-800">
                                  Suspended
                                </span>
                              )}
                              {!previewApproved.has(tenant.tenantId) && (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-amber-100 text-amber-800">
                                  Waitlist
                                </span>
                              )}
                              {tenant.planTier === "pro" || tenant.planTier === "enterprise" ? (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-purple-100 text-purple-800">
                                  Pro
                                </span>
                              ) : (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800">
                                  Community
                                </span>
                              )}
                              {tenant.validateAutopilotDevice && (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                                  Ready
                                </span>
                              )}
                              {legacyConfigured() && <HomingBadge clientId={tenant.homedAppClientId} />}
                            </div>
                            <div className="flex items-center gap-2">
                              <button
                                onClick={() => handleTogglePreview(tenant.tenantId)}
                                disabled={!canMutate || togglingPreviewTenant === tenant.tenantId}
                                className={`px-3 py-2 text-sm rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                  previewApproved.has(tenant.tenantId)
                                    ? 'bg-amber-500 text-white hover:bg-amber-600'
                                    : 'bg-green-600 text-white hover:bg-green-700'
                                }`}
                              >
                                {togglingPreviewTenant === tenant.tenantId
                                  ? "..."
                                  : previewApproved.has(tenant.tenantId)
                                  ? "Revoke"
                                  : "Approve"}
                              </button>
                              <button
                                onClick={async () => {
                                  setEditingTenant(tenant);
                                  // Seed from the list's address map so the field is filled
                                  // immediately; the per-tenant read below stays authoritative.
                                  setNotificationEmail(
                                    notificationEmailFor(notificationEmails, tenant.tenantId) ?? "");
                                  try {
                                    const resp = await authenticatedFetch(
                                      api.preview.notificationEmailTenant(tenant.tenantId),
                                      getAccessToken
                                    );
                                    if (resp.ok) {
                                      const data = await resp.json();
                                      setNotificationEmail(data.email || "");
                                    }
                                  } catch { /* best-effort */ }
                                }}
                                className="px-4 py-2 text-sm bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
                              >
                                Edit
                              </button>
                            </div>
                          </div>
                        </div>
                      </div>
                    ))}

                    {/* Pagination */}
                    {totalPages > 1 && (
                      <div className="flex items-center justify-between pt-4 border-t border-gray-200">
                        <button
                          onClick={() => setCurrentPage(p => Math.max(0, p - 1))}
                          disabled={currentPage === 0}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Previous
                        </button>
                        <span className="text-sm text-gray-600">
                          Page {currentPage + 1} of {totalPages}
                        </span>
                        <button
                          onClick={() => setCurrentPage(p => Math.min(totalPages - 1, p + 1))}
                          disabled={currentPage >= totalPages - 1}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Next
                        </button>
                      </div>
                    )}
                  </>
                )}
              </div>

              {/* Statistics */}
              {totalCount > 0 && (
                <div className="pt-3 border-t border-green-200 flex items-center justify-between gap-4 text-sm text-gray-600 flex-wrap">
                  <span>
                    <span className="font-semibold text-blue-700">{readyCount}</span>
                    {' '}of{' '}
                    <span className="font-semibold">{totalCount}</span>
                    {' '}Tenant(s) are Ready
                  </span>
                  <span>
                    <span className="font-semibold text-amber-600">{waitlistCount}</span>
                    {' '}of{' '}
                    <span className="font-semibold">{totalCount}</span>
                    {' '}Tenant(s) are on the Waitlist
                  </span>
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Edit Tenant Modal */}
      {editingTenant && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full max-h-[90vh] overflow-y-auto">
            <div className="sticky top-0 z-10 bg-green-600 text-white p-6 rounded-t-lg">
              <h2 className="text-2xl font-bold">Edit Tenant Configuration</h2>
              <p className="text-green-100 text-sm mt-1">{editingTenant.tenantId}</p>
            </div>

            <div className="p-6 space-y-6">
              {/* Tenant Suspension */}
              <div className="bg-red-50 border border-red-200 rounded-lg p-4">
                <h3 className="font-semibold text-red-900 mb-1">Tenant Suspension</h3>
                <p className="text-xs text-gray-600 mb-3">
                  Blocks sign-in and tenant auto-activation while the tenant&apos;s data stays in
                  place — this is the durable lock-out lever for abuse cases (offboarding below is
                  not: it deletes the suspension along with everything else).
                </p>
                <div className="space-y-3">
                  <label className="flex items-center space-x-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={editingTenant.disabled}
                      onChange={(e) => setEditingTenant({ ...editingTenant, disabled: e.target.checked })}
                      className="w-4 h-4 text-red-600 border-gray-300 rounded focus:ring-red-500"
                    />
                    <span className="text-sm font-medium text-gray-700">Suspend Tenant</span>
                  </label>

                  {editingTenant.disabled && (
                    <>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Reason</label>
                        <input
                          type="text"
                          value={editingTenant.disabledReason || ''}
                          onChange={(e) => setEditingTenant({ ...editingTenant, disabledReason: e.target.value })}
                          placeholder="Optional: Why is this tenant suspended?"
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-red-500 focus:border-red-500"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Disabled Until</label>
                        <input
                          type="datetime-local"
                          value={editingTenant.disabledUntil ? new Date(editingTenant.disabledUntil).toISOString().slice(0, 16) : ''}
                          onChange={(e) => setEditingTenant({ ...editingTenant, disabledUntil: e.target.value ? new Date(e.target.value).toISOString() : undefined })}
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-red-500 focus:border-red-500"
                        />
                        <p className="text-xs text-gray-500 mt-1">Optional: Auto-enable after this date/time</p>
                      </div>
                    </>
                  )}
                </div>
              </div>

              {/* Plan & Trial (own save path — PATCH plan endpoint; the modal's generic Save
                  does not touch these fields, the backend preserves them on PUT) */}
              <div className="bg-purple-50 border border-purple-200 rounded-lg p-4">
                <div className="flex items-center justify-between mb-3">
                  <h3 className="font-semibold text-purple-900">Plan &amp; Trial</h3>
                  {(() => {
                    const isProTier = editingTenant.planTier === "pro" || editingTenant.planTier === "enterprise";
                    const trialActive = !!editingTenant.trialExpiresUtc &&
                      new Date(editingTenant.trialExpiresUtc).getTime() > nowMs;
                    const effective = isProTier || trialActive ? "Pro" : "Community";
                    return (
                      <span className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${
                        effective === "Pro"
                          ? "bg-purple-100 text-purple-800"
                          : "bg-gray-100 text-gray-700"
                      }`}>
                        Effective: {effective}{!isProTier && trialActive ? " (Trial)" : ""}
                      </span>
                    );
                  })()}
                </div>
                <div className="space-y-3">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Plan Tier</label>
                    <select
                      value={editingTenant.planTier === "pro" || editingTenant.planTier === "enterprise" ? "pro" : "community"}
                      onChange={(e) => setEditingTenant({ ...editingTenant, planTier: e.target.value })}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
                    >
                      <option value="community">Community</option>
                      <option value="pro">Pro</option>
                    </select>
                    {editingTenant.planTier === "enterprise" && (
                      <p className="text-xs text-amber-600 mt-1">
                        Stored legacy tier &quot;enterprise&quot; resolves to Pro. Saving normalizes it to &quot;pro&quot;.
                      </p>
                    )}
                    {editingTenant.planTier && !["pro", "enterprise", "community"].includes(editingTenant.planTier) && (
                      <p className="text-xs text-amber-600 mt-1">
                        Stored legacy tier &quot;{editingTenant.planTier}&quot; resolves to Community. Saving normalizes it.
                      </p>
                    )}
                    {(editingTenant.planTier === "pro" || editingTenant.planTier === "enterprise") && (!editingTenant.contactEmail || !editingTenant.companyName) && (
                      <p className="text-xs text-amber-600 mt-1">
                        ⚠ Contact profile incomplete ({[!editingTenant.contactEmail && "no contact address", !editingTenant.companyName && "no company name"].filter(Boolean).join(", ")}) —
                        Pro tenants should be reachable and identifiable for service and security
                        matters. Self-service trials are blocked until both are set; GA assignment is
                        not, and the tenant sees a dashboard reminder until an admin completes it
                        (Settings → Tenant → Contact).
                      </p>
                    )}
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Trial Ends (UTC)</label>
                    <div className="flex items-center gap-2">
                      <input
                        type="datetime-local"
                        value={editingTenant.trialExpiresUtc ? new Date(editingTenant.trialExpiresUtc).toISOString().slice(0, 16) : ""}
                        onChange={(e) => setEditingTenant({
                          ...editingTenant,
                          trialExpiresUtc: e.target.value ? new Date(e.target.value + "Z").toISOString() : null,
                        })}
                        className="flex-1 px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
                      />
                      {editingTenant.trialExpiresUtc && (
                        <button
                          onClick={() => setEditingTenant({ ...editingTenant, trialExpiresUtc: null })}
                          className="px-3 py-2 text-sm text-gray-600 border border-gray-300 rounded-lg hover:bg-gray-50 transition-colors"
                          title="End the trial (saves as no trial)"
                        >
                          Clear
                        </button>
                      )}
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      Set a date to grant/extend a Pro trial; clear to end it. Saving does not reset trial consumption.
                    </p>
                  </div>
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Delegated tenant slots (override)</label>
                    <div className="flex items-center gap-2">
                      <input
                        type="number"
                        min={0}
                        value={editingTenant.maxDelegatedTenantsOverride ?? ""}
                        onChange={(e) => setEditingTenant({
                          ...editingTenant,
                          maxDelegatedTenantsOverride: e.target.value === "" ? null : Math.max(0, Math.floor(Number(e.target.value))),
                        })}
                        placeholder={slotUsage ? `plan: ${slotUsage.catalogLimit}` : "plan default"}
                        className="w-32 px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
                      />
                      {editingTenant.maxDelegatedTenantsOverride != null && (
                        <button
                          onClick={() => setEditingTenant({ ...editingTenant, maxDelegatedTenantsOverride: null })}
                          className="px-3 py-2 text-sm text-gray-600 border border-gray-300 rounded-lg hover:bg-gray-50 transition-colors"
                          title="Clear the override (the plan entitlement applies)"
                        >
                          Clear
                        </button>
                      )}
                      {slotUsage && (
                        <span className="text-xs text-gray-600">
                          Slots: {slotUsage.used} of {slotUsage.limit} in use
                          {slotUsage.pendingInvitations > 0 && ` (${slotUsage.pendingInvitations} pending)`}
                          {slotUsage.holds.length > 0 && ` (${slotUsage.holds.length} on hold)`}
                        </span>
                      )}
                    </div>
                    {slotUsage && slotUsage.holds.length > 0 && (
                      <ul className="mt-2 space-y-1">
                        {slotUsage.holds.map((h) => (
                          <li key={h.invitationId} className="flex flex-wrap items-center gap-x-2 text-xs text-gray-600">
                            <span className="font-mono">{h.tenantId ?? "unknown tenant"}</span>
                            <span>held until {new Date(h.holdUntilUtc).toLocaleString()}</span>
                            <span className="text-gray-400">by {h.releasedBy}</span>
                            <button
                              type="button"
                              disabled={!canMutate || releasingHold === h.invitationId}
                              onClick={() => handleReleaseHold(editingTenant.tenantId, h.invitationId)}
                              className="ml-auto text-purple-700 hover:underline disabled:opacity-50"
                              title="End this 24 h hold now (support escape hatch; audited)"
                            >
                              {releasingHold === h.invitationId ? "Releasing…" : "Release now"}
                            </button>
                          </li>
                        ))}
                      </ul>
                    )}
                    <p className="text-xs text-gray-500 mt-1">
                      How many distinct customer tenants this (MSP) tenant&rsquo;s users may manage. Blank = plan entitlement
                      (Community 0, Pro 2); a value applies regardless of plan, using delegation still requires Pro.
                    </p>
                  </div>
                  <div className="flex items-center justify-between">
                    <span className="text-xs text-gray-500">
                      {editingTenant.trialConsumed
                        ? "Self-service trial: already consumed (re-grants only via this panel)."
                        : "Self-service trial: still available to the tenant."}
                    </span>
                    <button
                      onClick={() => handleSavePlan(editingTenant)}
                      disabled={!canMutate || savingPlan}
                      className="px-3 py-2 text-sm font-medium text-white bg-purple-600 rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5"
                    >
                      {savingPlan ? (
                        <>
                          <div className="animate-spin rounded-full h-3.5 w-3.5 border-b-2 border-white"></div>
                          <span>Saving…</span>
                        </>
                      ) : (
                        <span>Save Plan</span>
                      )}
                    </button>
                  </div>
                </div>
              </div>

              {/* App Registration Homing (own save path — POST app-homing endpoint; the modal's
                  generic Save does not touch this field, the backend preserves it on PUT) */}
              {legacyConfigured() && (
                <div className="bg-sky-50 border border-sky-200 rounded-lg p-4">
                  <div className="flex items-center justify-between mb-3">
                    <h3 className="font-semibold text-sky-900">App Registration Homing</h3>
                    <HomingBadge clientId={editingTenant.homedAppClientId} />
                  </div>
                  <div className="space-y-3">
                    <p className="text-xs text-gray-600">
                      {editingTenant.lastAuthClientId ? (
                        <>
                          Logins arrive via the{" "}
                          <span className="font-medium">
                            {classifyClientId(editingTenant.lastAuthClientId) === "primary" ? "new" : "legacy"} app
                          </span>
                          {editingTenant.lastAuthClientIdSince && (
                            <> since {new Date(editingTenant.lastAuthClientIdSince).toLocaleString()}</>
                          )}
                          .
                        </>
                      ) : (
                        <>No login provenance recorded yet.</>
                      )}
                    </p>
                    {editingTenant.entraAppRolesEnabled && (
                      <p className="text-xs text-amber-700 bg-amber-50 border border-amber-200 rounded-lg p-2">
                        Entra app roles are enabled — switching requires re-assigning the app roles on
                        the other enterprise app, or those users lose their role claims.
                      </p>
                    )}
                    <div className="flex justify-end">
                      {classifyClientId(editingTenant.homedAppClientId) === "primary" ? (
                        <button
                          onClick={() => { setHomingError(null); setHomingDialogTarget("legacy"); }}
                          disabled={!canMutate || savingHoming}
                          className="px-3 py-2 text-sm font-medium text-white bg-amber-600 rounded-lg hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Revert to legacy app
                        </button>
                      ) : (
                        <button
                          onClick={() => { setHomingError(null); setHomingDialogTarget("primary"); }}
                          disabled={!canMutate || savingHoming}
                          className="px-3 py-2 text-sm font-medium text-white bg-sky-600 rounded-lg hover:bg-sky-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Switch to new app
                        </button>
                      )}
                    </div>
                  </div>
                </div>
              )}

              {/* Admin Users Info */}
              <TenantAdminSection
                tenantId={editingTenant.tenantId}
                getAccessToken={getAccessToken}
                setError={setError}
                setSuccessMessage={setSuccessMessage}
              />

              {/* Identity bindings homed in this tenant (cross-tenant role holders) — collapsed, inspect/correct only */}
              <IdentityBindingsSection
                tenantId={editingTenant.tenantId}
                getAccessToken={getAccessToken}
                setError={setError}
                setSuccessMessage={setSuccessMessage}
              />

              {/* Preview Notification Email */}
              <div className="bg-indigo-50 border border-indigo-200 rounded-lg p-4">
                <h3 className="font-semibold text-indigo-900 mb-3">Preview Notification Email</h3>
                <div className="flex flex-wrap items-center gap-2">
                  <input
                    type="email"
                    value={notificationEmail}
                    onChange={(e) => setNotificationEmail(e.target.value)}
                    placeholder="user@example.com"
                    className="flex-1 min-w-0 px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 transition-colors"
                  />
                  <button
                    onClick={() => handleSendWelcomeEmail(editingTenant.tenantId, notificationEmail)}
                    disabled={!canMutate || sendingWelcomeEmail || !notificationEmail.trim()}
                    className="px-3 py-2 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap flex items-center gap-1.5"
                    title="Send or resend the activation welcome email"
                  >
                    {sendingWelcomeEmail ? (
                      <>
                        <div className="animate-spin rounded-full h-3.5 w-3.5 border-b-2 border-white"></div>
                        <span>Sending...</span>
                      </>
                    ) : (
                      <>
                        <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                        </svg>
                        <span>Send Welcome Email</span>
                      </>
                    )}
                  </button>
                </div>
                <p className="text-xs text-indigo-600 mt-2">
                  The email is saved and sent in one step. Also sent automatically on approval if set.
                </p>
                {(editingTenant.contactEmail || editingTenant.companyName) && (
                  <p className="text-xs text-indigo-700 mt-2 pt-2 border-t border-indigo-200">
                    <span className="font-medium">Tenant contact:</span>{" "}
                    {editingTenant.companyName && <span>{editingTenant.companyName}{editingTenant.contactEmail ? ", " : ""}</span>}
                    {editingTenant.contactEmail && <span className="font-mono">{editingTenant.contactEmail}</span>}
                    <span className="text-indigo-500"> — maintained by the tenant, for service matters only</span>
                  </p>
                )}
              </div>

              {/* Device API Rate Limit override */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Device API Rate Limit (Requests/Min)</label>
                <input
                  type="number"
                  min="1"
                  max="10000"
                  placeholder="Blank = inherit global default"
                  value={editingTenant.customRateLimitRequestsPerMinute ?? ""}
                  onChange={(e) => {
                    const v = e.target.value.trim();
                    setEditingTenant({ ...editingTenant, customRateLimitRequestsPerMinute: v === "" ? null : (parseInt(v) || null) });
                  }}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 transition-colors"
                />
                <p className="mt-1 text-xs text-gray-500">Per-device (agent/cert) limit. Leave blank to inherit the global default.</p>
              </div>

              {/* User API Rate Limit override */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">User API Rate Limit (Requests/Min)</label>
                <input
                  type="number"
                  min="1"
                  max="10000"
                  placeholder="Blank = inherit global default"
                  value={editingTenant.customUserRateLimitRequestsPerMinute ?? ""}
                  onChange={(e) => {
                    const v = e.target.value.trim();
                    setEditingTenant({ ...editingTenant, customUserRateLimitRequestsPerMinute: v === "" ? null : (parseInt(v) || null) });
                  }}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 transition-colors"
                />
                <p className="mt-1 text-xs text-gray-500">Per-user (portal) limit for standard users. Leave blank to inherit the global default. Does not apply to Global Admins.</p>
              </div>

              <label className="flex items-center space-x-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={editingTenant.bootstrapTokenEnabled ?? false}
                  onChange={(e) => setEditingTenant({ ...editingTenant, bootstrapTokenEnabled: e.target.checked })}
                  className="w-4 h-4 text-teal-600 border-gray-300 rounded focus:ring-teal-500"
                />
                <span className="text-sm font-medium text-gray-700">Enable Bootstrap Token</span>
              </label>

              <label className="flex items-center space-x-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={editingTenant.unrestrictedModeEnabled ?? false}
                  onChange={(e) => setEditingTenant({ ...editingTenant, unrestrictedModeEnabled: e.target.checked })}
                  className="w-4 h-4 text-teal-600 border-gray-300 rounded focus:ring-teal-500"
                />
                <span className="text-sm font-medium text-gray-700">Enable Unrestricted Mode</span>
              </label>

              <div>
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingTenant.enableEspContinueAnywayObservation ?? false}
                    onChange={(e) => setEditingTenant({ ...editingTenant, enableEspContinueAnywayObservation: e.target.checked })}
                    className="w-4 h-4 text-teal-600 border-gray-300 rounded focus:ring-teal-500"
                  />
                  <span className="text-sm font-medium text-gray-700">Enable Continue-Anyway Observation</span>
                </label>
                <p className="text-xs text-gray-400 mt-1 ml-6">
                  Device-phase ESP terminal failures on profiles that allow &quot;Continue anyway&quot; are
                  observed for up to 60 min instead of failing immediately: a real-user desktop completes
                  the session as Succeeded with an amber &quot;with issues&quot; badge; otherwise it fails with
                  the original reason. Needs agent with ConfigVersion 37+; applies to new sessions only.
                </p>
              </div>

              <div>
                <label className="flex items-center space-x-2 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingTenant.entraAppRolesEnabled ?? false}
                    onChange={(e) => setEditingTenant({ ...editingTenant, entraAppRolesEnabled: e.target.checked })}
                    className="w-4 h-4 text-teal-600 border-gray-300 rounded focus:ring-teal-500"
                  />
                  <span className="text-sm font-medium text-gray-700">Enable Entra App Roles</span>
                </label>
                <p className="text-xs text-gray-400 mt-1 ml-6">
                  Allow Admin/Operator roles to be granted via Entra app-role assignments on the Enterprise App (the token&apos;s roles claim), in addition to the member table. The member table always wins.
                </p>
              </div>

              {/* Data Management */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Data Retention (Days)</label>
                <input
                  type="number"
                  min="0"
                  value={editingTenant.dataRetentionDays}
                  onChange={(e) => {
                    const val = parseInt(e.target.value);
                    setEditingTenant({ ...editingTenant, dataRetentionDays: isNaN(val) ? 90 : val });
                  }}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 transition-colors"
                />
                {editingTenant.dataRetentionDays === 0 ? (
                  <p className="text-xs text-amber-600 mt-1 font-medium">⚠ Infinite retention — data will never be automatically deleted</p>
                ) : (editingTenant.dataRetentionDays < 7 || editingTenant.dataRetentionDays > 365) ? (
                  <p className="text-xs text-amber-600 mt-1 font-medium">⚠ Outside tenant range (7–365) — field will be locked for tenant admins</p>
                ) : (
                  <p className="text-xs text-gray-400 mt-1">Tenant range: 7–90 (Community) / 7–365 (Pro). Values above the plan cap are enforced at the cap. Set 0 for infinite retention (Global only).</p>
                )}
              </div>

              {/* Danger Zone — offboarding cascade (own path: DELETE tenants/{id}/offboard;
                  deliberately NOT part of the modal's Save button) */}
              <div className="bg-red-50 border-2 border-red-300 rounded-lg p-4">
                <h3 className="font-semibold text-red-900 mb-1">Offboard Tenant</h3>
                <p className="text-xs text-gray-600 mb-2">
                  Suspends the tenant immediately and permanently deletes all of its data
                  (sessions, events, rules, admins, configuration) after a short drain window.
                  Same cascade as the tenant&apos;s self-service offboarding.
                </p>
                <p className="text-xs text-red-700 font-medium mb-3">
                  Not a ban: the deletion includes the suspension, so once the cascade completes a
                  new sign-in re-onboards (and auto-activates) the tenant. To lock a tenant out,
                  suspend it above and leave its data in place.
                </p>
                <button
                  onClick={() => { setOffboardError(null); setOffboardDialogOpen(true); }}
                  disabled={!canMutate || offboarding}
                  className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 text-sm font-medium"
                >
                  Offboard Tenant…
                </button>
              </div>

            </div>

            {/* Modal Actions */}
            <div className="sticky bottom-0 bg-gray-50 px-6 py-4 border-t border-gray-200 rounded-b-lg flex justify-end space-x-3">
              <button
                onClick={() => setEditingTenant(null)}
                disabled={savingTenant}
                className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={() => handleSaveTenant(editingTenant)}
                disabled={!canMutate || savingTenant}
                className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 disabled:opacity-50 flex items-center space-x-2"
              >
                {savingTenant ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Saving...</span>
                  </>
                ) : (
                  <span>Save Changes</span>
                )}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Offboarding confirmation (renders above the editor modal) */}
      {editingTenant && offboardDialogOpen && (
        <OffboardTenantConfirmDialog
          tenantLabel={editingTenant.domainName || editingTenant.tenantId}
          tenantId={editingTenant.tenantId}
          saving={offboarding}
          error={offboardError}
          onCancel={() => setOffboardDialogOpen(false)}
          onConfirm={() => handleOffboardTenant(editingTenant)}
        />
      )}

      {/* App-homing flip confirmation (renders above the editor modal) */}
      {editingTenant && homingDialogTarget && (
        <AppHomingConfirmDialog
          tenantLabel={editingTenant.domainName || editingTenant.tenantId}
          target={homingDialogTarget}
          entraAppRolesEnabled={editingTenant.entraAppRolesEnabled ?? false}
          saving={savingHoming}
          error={homingError}
          onCancel={() => setHomingDialogTarget(null)}
          onConfirm={(force) => handleFlipHoming(editingTenant, homingDialogTarget, force)}
        />
      )}
    </>
  );
}
