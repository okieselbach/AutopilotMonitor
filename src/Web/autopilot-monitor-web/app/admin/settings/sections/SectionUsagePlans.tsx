"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../../contexts/AuthContext";
import { useAdminConfig } from "../../AdminConfigContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";

interface PlanTierDefinition {
  name: string;
  dailyRequestLimit: number;
  monthlyRequestLimit: number;
  description: string;
}

const DEFAULT_TIER: PlanTierDefinition = {
  name: "",
  dailyRequestLimit: 100,
  monthlyRequestLimit: 3000,
  description: "",
};

export function SectionUsagePlans() {
  const { getAccessToken } = useAuth();
  const { tenants, userRateLimit } = useAdminConfig();
  const [tiers, setTiers] = useState<PlanTierDefinition[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [hasChanges, setHasChanges] = useState(false);

  const fetchTiers = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);
      const res = await authenticatedFetch(api.mcpUsage.planTiers(), getAccessToken);
      if (!res.ok) throw new Error(`Failed to load plan tiers: ${res.status}`);
      const data = await res.json();
      setTiers(data.tiers || []);
      setHasChanges(false);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired. Please refresh the page.");
      } else {
        setError(err instanceof Error ? err.message : "Failed to load plan tiers");
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  useEffect(() => {
    fetchTiers();
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

      const res = await authenticatedFetch(api.mcpUsage.planTiers(), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ tiers }),
      });

      if (!res.ok) {
        const data = await res.json();
        throw new Error(data.error || `Failed to save: ${res.status}`);
      }

      const data = await res.json();
      setTiers(data.tiers || tiers);
      setHasChanges(false);
      setSuccessMessage("Plan tier definitions saved successfully.");
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        setError("Session expired. Please refresh the page.");
      } else {
        setError(err instanceof Error ? err.message : "Failed to save plan tiers");
      }
    } finally {
      setSaving(false);
    }
  }, [tiers, getAccessToken]);

  const updateTier = (index: number, field: keyof PlanTierDefinition, value: string | number) => {
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

  if (loading) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-indigo-600" />
        <span className="ml-3 text-sm text-gray-500">Loading plan definitions...</span>
      </div>
    );
  }

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
            className="px-4 py-2 text-sm bg-white border border-gray-300 rounded-md hover:bg-gray-50 transition-colors"
          >
            + Add Tier
          </button>
          <button
            onClick={handleSave}
            disabled={saving || !hasChanges}
            className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-md hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
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
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
          <p className="text-sm text-blue-800">
            Users without a per-user plan override inherit their tenant&apos;s effective user rate limit
            ({tenants[0]?.customUserRateLimitRequestsPerMinute ?? userRateLimit} req/min).
          </p>
        </div>
      )}

      {/* Empty State */}
      {tiers.length === 0 && (
        <div className="bg-white rounded-lg shadow p-12 text-center">
          <p className="text-gray-500 mb-4">No plan tiers defined yet.</p>
          <button
            onClick={addTier}
            className="px-4 py-2 text-sm bg-indigo-600 text-white rounded-md hover:bg-indigo-700 transition-colors"
          >
            Create First Plan Tier
          </button>
        </div>
      )}

      {/* Tier Cards */}
      <div className="space-y-4">
        {tiers.map((tier, index) => (
          <div key={index} className="bg-white rounded-lg shadow p-6">
            <div className="flex items-start justify-between mb-4">
              <div className="flex-1 grid grid-cols-1 md:grid-cols-4 gap-4">
                {/* Name */}
                <div>
                  <label className="block text-xs font-medium text-gray-500 mb-1">Tier Name</label>
                  <input
                    type="text"
                    value={tier.name}
                    onChange={(e) => updateTier(index, "name", e.target.value)}
                    placeholder="e.g., free, pro, enterprise"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
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
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
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
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
                  />
                </div>
                {/* Description */}
                <div>
                  <label className="block text-xs font-medium text-gray-500 mb-1">Description</label>
                  <input
                    type="text"
                    value={tier.description}
                    onChange={(e) => updateTier(index, "description", e.target.value)}
                    placeholder="Brief description"
                    className="w-full px-3 py-2 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-indigo-500"
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
        ))}
      </div>
    </div>
  );
}
