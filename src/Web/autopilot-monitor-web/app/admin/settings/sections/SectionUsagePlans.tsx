"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { useAdminConfig } from "../../AdminConfigContext";
import { apiErrorText, fetchJson, jsonBody } from "@/lib/apiClient";
import { api } from "@/lib/api";
import { TableSkeleton } from "@/components/skeletons/TableSkeleton";
import type {
  PlanTierDefinitionsResponse,
  SetPlanTierDefinitionsRequest,
  UsagePlanCatalogDefaults,
} from "@/utils/wire-types.generated";

interface PlanTierDefinition {
  name: string;
  dailyRequestLimit: number;
  monthlyRequestLimit: number;
  description: string;
  // Organization-wide windows shared by every member of a tenant on this plan. Absent = the edition's
  // built-in tenant limit applies; 0 = unlimited. Per-user plan overrides never lift these.
  tenantDailyRequestLimit?: number;
  tenantMonthlyRequestLimit?: number;
  // Growth per delegation slot bought beyond the ones the edition includes, added to every account window
  // (user) and to the organization windows (tenant) of the managing tenant. Absent = the edition's built-in
  // value; 0 = a purchased slot adds nothing.
  slotDailyRequestLimit?: number;
  slotMonthlyRequestLimit?: number;
  slotTenantDailyRequestLimit?: number;
  slotTenantMonthlyRequestLimit?: number;
}

/** The optional limits: blank on the wire ⇒ the catalog value of the tier's edition applies (same key there). */
type OptionalLimitField =
  | "tenantDailyRequestLimit"
  | "tenantMonthlyRequestLimit"
  | "slotDailyRequestLimit"
  | "slotMonthlyRequestLimit"
  | "slotTenantDailyRequestLimit"
  | "slotTenantMonthlyRequestLimit";

const OPTIONAL_LIMITS: ReadonlyArray<{ field: OptionalLimitField; label: string }> = [
  { field: "tenantDailyRequestLimit", label: "Tenant Daily Limit" },
  { field: "tenantMonthlyRequestLimit", label: "Tenant Monthly Limit" },
  { field: "slotDailyRequestLimit", label: "Slot Daily (user)" },
  { field: "slotMonthlyRequestLimit", label: "Slot Monthly (user)" },
  { field: "slotTenantDailyRequestLimit", label: "Slot Daily (tenant)" },
  { field: "slotTenantMonthlyRequestLimit", label: "Slot Monthly (tenant)" },
];

const DEFAULT_TIER: PlanTierDefinition = {
  name: "",
  dailyRequestLimit: 100,
  monthlyRequestLimit: 3000,
  description: "",
};

// Blank input = "not set" (undefined on the wire → catalog fallback); "0" stays 0 (= unlimited / no growth).
function parseOptionalLimit(raw: string): number | undefined {
  if (raw.trim() === "") return undefined;
  const n = parseInt(raw, 10);
  return Number.isNaN(n) ? undefined : Math.max(0, n);
}

/** The catalog edition a tier's built-in fallbacks come from: "community" by name, every other tier is Pro. */
function editionOf(tierName: string): "community" | "pro" {
  return tierName.trim().toLowerCase() === "community" ? "community" : "pro";
}

/** One compact line of an edition's built-in fallbacks, daily / monthly per window. */
function catalogLine(c: UsagePlanCatalogDefaults): string {
  const n = (v: number) => v.toLocaleString();
  return (
    `${c.edition}: user ${n(c.dailyRequestLimit)} / ${n(c.monthlyRequestLimit)}` +
    ` · tenant ${n(c.tenantDailyRequestLimit)} / ${n(c.tenantMonthlyRequestLimit)}` +
    ` · ${c.includedDelegatedSlots} included slot${c.includedDelegatedSlots === 1 ? "" : "s"}` +
    ` · per extra slot +${n(c.slotDailyRequestLimit)} / +${n(c.slotMonthlyRequestLimit)} user,` +
    ` +${n(c.slotTenantDailyRequestLimit)} / +${n(c.slotTenantMonthlyRequestLimit)} tenant`
  );
}

/** A limit that may stay blank; the placeholder shows the built-in value a blank field falls back to. */
function OptionalLimitInput({
  label,
  value,
  placeholder,
  onChange,
}: {
  label: string;
  value: number | undefined;
  placeholder: string;
  onChange: (value: number | undefined) => void;
}) {
  return (
    <div>
      <label className="block text-xs font-medium text-gray-500 mb-1">{label}</label>
      <input
        type="number"
        min={0}
        value={value ?? ""}
        onChange={(e) => onChange(parseOptionalLimit(e.target.value))}
        placeholder={placeholder}
        className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-green-500"
      />
    </div>
  );
}

export function SectionUsagePlans() {
  const { getAccessToken } = useAuth();
  const { ensureAdminConfigLoaded, ensureTenantsLoaded, tenants, userRateLimit } = useAdminConfig();

  useEffect(() => {
    ensureAdminConfigLoaded();
    ensureTenantsLoaded();
  }, [ensureAdminConfigLoaded, ensureTenantsLoaded]);
  const [tiers, setTiers] = useState<PlanTierDefinition[]>([]);
  // The built-in fallbacks per edition — what a blank optional field means; shown as the placeholders.
  const [catalog, setCatalog] = useState<UsagePlanCatalogDefaults[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [hasChanges, setHasChanges] = useState(false);

  const fetchTiers = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const data = await fetchJson<PlanTierDefinitionsResponse>(api.mcpUsage.planTiers(), getAccessToken);
      setTiers(data.tiers || []);
      setCatalog(data.catalog || []);
      setHasChanges(false);
    } catch (err) {
      setError(apiErrorText(err, "Failed to load plan tiers"));
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await fetchTiers();
    };
    void run();
  }, [fetchTiers]);

  const handleSave = useCallback(async () => {
    // Validate
    const names = tiers.map(t => t.name.trim().toLowerCase());
    if (names.some(n => !n)) {
      setError("All tiers must have a name.");
      return;
    }
    if (new Set(names).size !== names.length) {
      setError("Tier names must be unique.");
      return;
    }

    try {
      setSaving(true);
      setError(null);
      setSuccessMessage(null);

      const data = await fetchJson<PlanTierDefinitionsResponse>(api.mcpUsage.planTiers(), getAccessToken, {
        method: "PUT",
        body: jsonBody<SetPlanTierDefinitionsRequest>({ tiers }),
      });
      setTiers(data.tiers || tiers);
      if (data.catalog) setCatalog(data.catalog);
      setHasChanges(false);
      setSuccessMessage("Plan tier definitions saved successfully.");
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      setError(apiErrorText(err, "Failed to save plan tiers"));
    } finally {
      setSaving(false);
    }
  }, [tiers, getAccessToken]);

  const updateTier = (index: number, field: keyof PlanTierDefinition, value: string | number | undefined) => {
    setTiers(prev => {
      const updated = [...prev];
      updated[index] = { ...updated[index], [field]: value };
      return updated;
    });
    setHasChanges(true);
  };

  const addTier = () => {
    setTiers(prev => [...prev, { ...DEFAULT_TIER }]);
    setHasChanges(true);
  };

  const removeTier = (index: number) => {
    setTiers(prev => prev.filter((_, i) => i !== index));
    setHasChanges(true);
  };

  /** The built-in fallbacks behind a tier's blank fields (its edition by name; absent until loaded). */
  const defaultsFor = (tier: PlanTierDefinition) => catalog.find((c) => c.edition === editionOf(tier.name));

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-lg font-semibold text-gray-900">Usage Plans</h2>
          <p className="text-sm text-gray-500">
            Define MCP usage plan tiers with request limits. Assign plans to individual MCP users or let them inherit the tenant default.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={addTier}
            disabled={loading}
            className="px-4 py-2 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            + Add Tier
          </button>
          <button
            onClick={handleSave}
            disabled={saving || !hasChanges}
            className="px-4 py-2 text-sm bg-green-600 text-white rounded-md hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {saving ? "Saving..." : "Save Changes"}
          </button>
        </div>
      </div>

      {/* Notifications */}
      {successMessage && (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">
          {successMessage}
        </div>
      )}
      {error && (
        <div className="bg-red-50 border border-red-200 rounded-lg p-4 text-sm text-red-700">
          {error}
        </div>
      )}

      {/* Tenant Default Info */}
      {tenants.length > 0 && (
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4 text-sm text-blue-800 space-y-1">
          <p>
            Users without a per-user plan override inherit their tenant&apos;s effective MCP &amp; integrations rate limit
            ({tenants[0]?.customUserRateLimitRequestsPerMinute ?? userRateLimit} req/min); interactive portal sessions
            use the separate global portal budget.
          </p>
          <p>
            The per-user limits cap each account; the tenant limits cap the whole organization (all members
            together) and always follow the tenant&apos;s own plan, so a per-user override never widens them.
            Leave a tenant limit blank for the built-in default, 0 for unlimited.
          </p>
          <p>
            Slot values are added per delegation slot bought beyond the ones the edition includes (Pro includes 2):
            the user values to each account window of the managing tenant, the tenant values to its organization
            windows. Leave a slot value blank for the built-in default, 0 for no growth.
          </p>
        </div>
      )}

      {/* Initial load — header stays visible, tier list gets the skeleton */}
      {loading && <TableSkeleton wrapped columns={4} rows={3} header={false} />}

      {/* Empty State */}
      {!loading && tiers.length === 0 && (
        <div className="bg-white rounded-lg shadow p-12 text-center">
          <p className="text-gray-500 mb-4">No plan tiers defined yet.</p>
          <button
            onClick={addTier}
            className="px-4 py-2 text-sm bg-green-600 text-white rounded-md hover:bg-green-700 transition-colors"
          >
            Create First Plan Tier
          </button>
        </div>
      )}

      {/* Tier Cards */}
      <div className="space-y-4">
        {tiers.map((tier, index) => {
          const defaults = defaultsFor(tier);
          return (
            <div key={index} className="bg-white rounded-lg shadow p-6">
              <div className="flex items-start justify-between mb-4">
                <div className="flex-1 grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-4">
                  {/* Name */}
                  <div>
                    <label className="block text-xs font-medium text-gray-500 mb-1">Tier Name</label>
                    <input
                      type="text"
                      value={tier.name}
                      onChange={(e) => updateTier(index, "name", e.target.value)}
                      placeholder="e.g., community, pro"
                      className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-green-500"
                    />
                  </div>
                  {/* Daily Limit */}
                  <div>
                    <label className="block text-xs font-medium text-gray-500 mb-1">Daily Request Limit</label>
                    <input
                      type="number"
                      min={0}
                      value={tier.dailyRequestLimit}
                      onChange={(e) => updateTier(index, "dailyRequestLimit", parseInt(e.target.value) || 0)}
                      className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-green-500"
                    />
                  </div>
                  {/* Monthly Limit */}
                  <div>
                    <label className="block text-xs font-medium text-gray-500 mb-1">Monthly Request Limit</label>
                    <input
                      type="number"
                      min={0}
                      value={tier.monthlyRequestLimit}
                      onChange={(e) => updateTier(index, "monthlyRequestLimit", parseInt(e.target.value) || 0)}
                      className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-green-500"
                    />
                  </div>
                  {/* Tenant windows and per-slot growth — blank = the edition's built-in value (the placeholder) */}
                  {OPTIONAL_LIMITS.map(({ field, label }) => (
                    <OptionalLimitInput
                      key={field}
                      label={label}
                      value={tier[field]}
                      placeholder={defaults ? String(defaults[field]) : "default"}
                      onChange={(v) => updateTier(index, field, v)}
                    />
                  ))}
                  {/* Description */}
                  <div>
                    <label className="block text-xs font-medium text-gray-500 mb-1">Description</label>
                    <input
                      type="text"
                      value={tier.description}
                      onChange={(e) => updateTier(index, "description", e.target.value)}
                      placeholder="Brief description"
                      className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-green-500"
                    />
                  </div>
                </div>
                <button
                  onClick={() => removeTier(index)}
                  className="ml-4 p-1.5 text-red-400 hover:text-red-600 hover:bg-red-50 rounded transition-colors"
                  title="Remove tier"
                >
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                  </svg>
                </button>
              </div>
            </div>
          );
        })}
      </div>

      {/* Built-in defaults — what a blank optional field means, per edition */}
      {!loading && catalog.length > 0 && (
        <div className="text-xs text-gray-500 space-y-0.5">
          <p className="font-medium text-gray-700">Built-in defaults (blank field) — daily / monthly</p>
          {catalog.map((c) => (
            <p key={c.edition}>{catalogLine(c)}</p>
          ))}
        </div>
      )}
    </div>
  );
}
