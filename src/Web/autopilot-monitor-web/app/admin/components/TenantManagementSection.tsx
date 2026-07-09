"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { TenantAdminSection } from "./TenantAdminSection";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";

export interface TenantConfiguration {
  tenantId: string;
  domainName: string;
  lastUpdated: string;
  updatedBy: string;
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
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  planTier?: string;
  /** Enterprise-trial end (ISO, UTC). Null/undefined = no trial. Managed via PATCH plan. */
  trialExpiresUtc?: string | null;
  /** Whether the tenant has used its one self-service trial. */
  trialConsumed?: boolean;
}

export interface TenantManagementSectionProps {
  tenants: TenantConfiguration[];
  loadingTenants: boolean;
  fetchTenants: () => void;
  previewApproved: Set<string>;
  setPreviewApproved: React.Dispatch<React.SetStateAction<Set<string>>>;
  setTenants: React.Dispatch<React.SetStateAction<TenantConfiguration[]>>;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function TenantManagementSection({
  tenants,
  loadingTenants,
  fetchTenants,
  previewApproved,
  setPreviewApproved,
  setTenants,
  getAccessToken,
  setError,
  setSuccessMessage,
}: TenantManagementSectionProps) {
  // Read-only Global Readers may view tenants (incl. config report) but not edit them.
  const canMutate = useCanMutatePlatform();
  const [searchQuery, setSearchQuery] = useState("");
  const [showOnlyWaitlist, setShowOnlyWaitlist] = useState(false);
  const [showOnlyReady, setShowOnlyReady] = useState(false);
  const [tenantSectionExpanded, setTenantSectionExpanded] = useState(false);
  const [editingTenant, setEditingTenant] = useState<TenantConfiguration | null>(null);
  const [savingTenant, setSavingTenant] = useState(false);
  const [savingPlan, setSavingPlan] = useState(false);
  const [currentPage, setCurrentPage] = useState(0);
  const tenantsPerPage = tenantSectionExpanded ? 7 : 3;

  // Preview Whitelist state
  const [togglingPreviewTenant, setTogglingPreviewTenant] = useState<string | null>(null);
  const [sendingWelcomeEmail, setSendingWelcomeEmail] = useState(false);
  const [notificationEmail, setNotificationEmail] = useState("");

  // Filter and sort tenants.
  // Quoted queries ("microsoft.com") do an exact, case-insensitive equality
  // match against tenantId/domainName instead of a substring match. This lets
  // you find a tenant like "microsoft.com" that would otherwise be swallowed by
  // every "*.onmicrosoft.com" domain in a plain substring search.
  const rawQuery = searchQuery.trim();
  const quotedMatch = rawQuery.match(/^"(.*)"$/);
  const exactTerm = quotedMatch ? quotedMatch[1].toLowerCase() : null;
  const substringTerm = searchQuery.toLowerCase();
  const filteredTenants = tenants.filter(t => {
    const matchesSearch = exactTerm !== null
      ? t.tenantId.toLowerCase() === exactTerm ||
        t.domainName.toLowerCase() === exactTerm
      : t.tenantId.toLowerCase().includes(substringTerm) ||
        t.domainName.toLowerCase().includes(substringTerm);
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

  // Reset to first page when search changes
  useEffect(() => {
    setCurrentPage(0);
  }, [searchQuery, showOnlyWaitlist, showOnlyReady]);

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
          // Legacy stored tiers (free/pro) resolve to Community server-side; the select only
          // offers the two canonical values, so normalize on save.
          planTier: tenant.planTier === "enterprise" ? "enterprise" : "community",
          trialExpiresUtc: tenant.trialExpiresUtc ?? null,
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
      });
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
                    placeholder={'Search by domain or tenant ID (use "..." for exact match)'}
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    className="w-full pl-10 pr-10 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
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
                      ? 'bg-blue-600 text-white border-blue-600'
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
                              {previewApproved.has(tenant.tenantId) ? (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800">
                                  Preview
                                </span>
                              ) : (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-amber-100 text-amber-800">
                                  Waitlist
                                </span>
                              )}
                              {tenant.validateAutopilotDevice && (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                                  Ready
                                </span>
                              )}
                            </div>
                            <div className="flex items-center gap-2">
                              <button
                                onClick={() => handleTogglePreview(tenant.tenantId)}
                                disabled={!canMutate || togglingPreviewTenant === tenant.tenantId}
                                className={`px-3 py-2 text-sm rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                  previewApproved.has(tenant.tenantId)
                                    ? 'bg-amber-500 text-white hover:bg-amber-600'
                                    : 'bg-blue-600 text-white hover:bg-blue-700'
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
                                  setNotificationEmail("");
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
                <h3 className="font-semibold text-red-900 mb-3">Tenant Suspension</h3>
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
                    const isEnterpriseTier = editingTenant.planTier === "enterprise";
                    const trialActive = !!editingTenant.trialExpiresUtc &&
                      new Date(editingTenant.trialExpiresUtc).getTime() > Date.now();
                    const effective = isEnterpriseTier || trialActive ? "Enterprise" : "Community";
                    return (
                      <span className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ${
                        effective === "Enterprise"
                          ? "bg-purple-100 text-purple-800"
                          : "bg-gray-100 text-gray-700"
                      }`}>
                        Effective: {effective}{!isEnterpriseTier && trialActive ? " (Trial)" : ""}
                      </span>
                    );
                  })()}
                </div>
                <div className="space-y-3">
                  <div>
                    <label className="block text-sm font-medium text-gray-700 mb-1">Plan Tier</label>
                    <select
                      value={editingTenant.planTier === "enterprise" ? "enterprise" : "community"}
                      onChange={(e) => setEditingTenant({ ...editingTenant, planTier: e.target.value })}
                      className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
                    >
                      <option value="community">Community</option>
                      <option value="enterprise">Enterprise</option>
                    </select>
                    {editingTenant.planTier && editingTenant.planTier !== "enterprise" && editingTenant.planTier !== "community" && (
                      <p className="text-xs text-amber-600 mt-1">
                        Stored legacy tier &quot;{editingTenant.planTier}&quot; resolves to Community. Saving normalizes it.
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
                      Set a date to grant/extend an Enterprise trial; clear to end it. Saving does not reset trial consumption.
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

              {/* Admin Users Info */}
              <TenantAdminSection
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
                    className="flex-1 min-w-0 px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                  />
                  <button
                    onClick={() => handleSendWelcomeEmail(editingTenant.tenantId, notificationEmail)}
                    disabled={!canMutate || sendingWelcomeEmail || !notificationEmail.trim()}
                    className="px-3 py-2 text-sm font-medium text-white bg-indigo-600 rounded-lg hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap flex items-center gap-1.5"
                    title="Send or resend the Private Preview welcome email"
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
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
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
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
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
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
                {editingTenant.dataRetentionDays === 0 ? (
                  <p className="text-xs text-amber-600 mt-1 font-medium">⚠ Infinite retention — data will never be automatically deleted</p>
                ) : (editingTenant.dataRetentionDays < 7 || editingTenant.dataRetentionDays > 365) ? (
                  <p className="text-xs text-amber-600 mt-1 font-medium">⚠ Outside tenant range (7–365) — field will be locked for tenant admins</p>
                ) : (
                  <p className="text-xs text-gray-400 mt-1">Tenant range: 7–90 (Community) / 7–365 (Enterprise). Values above the plan cap are enforced at the cap. Set 0 for infinite retention (Global only).</p>
                )}
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
    </>
  );
}
