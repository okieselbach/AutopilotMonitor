"use client";

import { useCallback, useEffect, useState } from "react";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { api } from "@/lib/api";

interface McpUser {
  upn: string;
  isEnabled: boolean;
  addedAt: string;
  addedBy: string;
  usagePlan: string | null;
}

interface PlanTierDefinition {
  name: string;
  dailyRequestLimit: number;
  monthlyRequestLimit: number;
  description: string;
}

type McpPolicy = "Disabled" | "WhitelistOnly" | "AllMembers";

const POLICY_LABELS: Record<McpPolicy, string> = {
  Disabled: "Disabled",
  WhitelistOnly: "Whitelist Only",
  AllMembers: "All Members",
};

const POLICY_DESCRIPTIONS: Record<McpPolicy, string> = {
  Disabled: "MCP access is completely disabled. No one can connect.",
  WhitelistOnly: "Only Global Admins and explicitly added MCP users can connect.",
  AllMembers: "Any authenticated user can connect to the MCP server.",
};

export default function McpUsersSection() {
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  const [users, setUsers] = useState<McpUser[]>([]);
  const [policy, setPolicy] = useState<McpPolicy>("WhitelistOnly");
  const [loading, setLoading] = useState(true);
  const [newEmail, setNewEmail] = useState("");
  const [adding, setAdding] = useState(false);
  const [removingUpn, setRemovingUpn] = useState<string | null>(null);
  const [togglingUpn, setTogglingUpn] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [currentPage, setCurrentPage] = useState(0);
  const [savingPolicy, setSavingPolicy] = useState(false);
  const [planTiers, setPlanTiers] = useState<PlanTierDefinition[]>([]);
  const [changingPlanUpn, setChangingPlanUpn] = useState<string | null>(null);

  const ITEMS_PER_PAGE = 5;

  const fetchMcpUsers = useCallback(async () => {
    try {
      setLoading(true);
      const response = await authenticatedFetch(api.mcpUsers.list(), getAccessToken);
      if (!response.ok) throw new Error(`Failed to load MCP users: ${response.statusText}`);
      const data = await response.json();
      setUsers(data.users ?? []);
      setPolicy(data.policy ?? "WhitelistOnly");
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        setError(err instanceof Error ? err.message : "Failed to load MCP users");
      }
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, addNotification]);

  const fetchPlanTiers = useCallback(async () => {
    try {
      const res = await authenticatedFetch(api.mcpUsage.planTiers(), getAccessToken);
      if (res.ok) {
        const data = await res.json();
        setPlanTiers(data.tiers || []);
      }
    } catch {
      // Plan tiers are optional — if we can't fetch them, just skip
    }
  }, [getAccessToken]);

  useEffect(() => {
    const run = async () => {
      await Promise.all([fetchMcpUsers(), fetchPlanTiers()]);
    };
    void run();
  }, [fetchMcpUsers, fetchPlanTiers]);

  const handlePolicyChange = useCallback(async (newPolicy: McpPolicy) => {
    try {
      setSavingPolicy(true);
      setError(null);
      setSuccessMessage(null);

      // Read current global config, update McpAccessPolicy, save back
      const getRes = await authenticatedFetch(api.globalConfig.get(), getAccessToken);
      if (!getRes.ok) throw new Error(`Failed to load global config: ${getRes.statusText}`);
      const config = await getRes.json();

      const saveRes = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ ...config, mcpAccessPolicy: newPolicy }),
      });

      if (!saveRes.ok) {
        const data = await saveRes.json();
        throw new Error(data.error || `Failed to save policy: ${saveRes.statusText}`);
      }

      setPolicy(newPolicy);
      setSuccessMessage(`MCP access policy changed to "${POLICY_LABELS[newPolicy]}".`);
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        setError(err instanceof Error ? err.message : "Failed to save policy");
      }
    } finally {
      setSavingPolicy(false);
    }
  }, [getAccessToken, addNotification]);

  const handleAddUser = useCallback(async () => {
    if (!newEmail.trim()) return;
    try {
      setAdding(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.mcpUsers.add(), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ upn: newEmail.trim() }),
      });

      if (!response.ok) {
        const data = await response.json();
        throw new Error(data.error || `Failed to add user: ${response.statusText}`);
      }

      setSuccessMessage(`MCP user ${newEmail} added successfully!`);
      setNewEmail("");
      await fetchMcpUsers();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        setError(err instanceof Error ? err.message : "Failed to add user");
      }
    } finally {
      setAdding(false);
    }
  }, [newEmail, getAccessToken, addNotification, fetchMcpUsers]);

  const handleRemoveUser = useCallback(async (upn: string) => {
    if (!confirm(`Remove ${upn} from MCP users?`)) return;
    try {
      setRemovingUpn(upn);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(api.mcpUsers.remove(upn), getAccessToken, {
        method: "DELETE",
      });

      if (!response.ok) {
        const data = await response.json();
        throw new Error(data.error || `Failed to remove user: ${response.statusText}`);
      }

      setSuccessMessage(`MCP user ${upn} removed.`);
      await fetchMcpUsers();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        setError(err instanceof Error ? err.message : "Failed to remove user");
      }
    } finally {
      setRemovingUpn(null);
    }
  }, [getAccessToken, addNotification, fetchMcpUsers]);

  const handleToggleUser = useCallback(async (upn: string, currentlyEnabled: boolean) => {
    try {
      setTogglingUpn(upn);
      setError(null);

      const url = currentlyEnabled ? api.mcpUsers.disable(upn) : api.mcpUsers.enable(upn);
      const response = await authenticatedFetch(url, getAccessToken, { method: "PATCH" });

      if (!response.ok) {
        const data = await response.json();
        throw new Error(data.error || `Failed to update user: ${response.statusText}`);
      }

      setSuccessMessage(`MCP user ${upn} ${currentlyEnabled ? "disabled" : "enabled"}.`);
      await fetchMcpUsers();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        setError(err instanceof Error ? err.message : "Failed to update user");
      }
    } finally {
      setTogglingUpn(null);
    }
  }, [getAccessToken, addNotification, fetchMcpUsers]);

  const handleSetUsagePlan = useCallback(async (upn: string, usagePlan: string) => {
    try {
      setChangingPlanUpn(upn);
      setError(null);

      const response = await authenticatedFetch(api.mcpUsers.setUsagePlan(upn), getAccessToken, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ usagePlan: usagePlan || null }),
      });

      if (!response.ok) {
        const data = await response.json();
        throw new Error(data.error || `Failed to set plan: ${response.statusText}`);
      }

      setSuccessMessage(`Usage plan for ${upn} updated.`);
      await fetchMcpUsers();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification("error", "Session Expired", err.message, "session-expired-error");
      } else {
        setError(err instanceof Error ? err.message : "Failed to set usage plan");
      }
    } finally {
      setChangingPlanUpn(null);
    }
  }, [getAccessToken, addNotification, fetchMcpUsers]);

  // Filter & paginate
  const filteredUsers = users.filter((u) =>
    u.upn.toLowerCase().includes(searchQuery.toLowerCase()),
  );
  const totalPages = Math.ceil(filteredUsers.length / ITEMS_PER_PAGE);
  const paginatedUsers = filteredUsers.slice(
    currentPage * ITEMS_PER_PAGE,
    (currentPage + 1) * ITEMS_PER_PAGE,
  );

  return (
    <div className="bg-white rounded-lg shadow">
      {/* Header */}
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-purple-50 to-indigo-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
              d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">MCP User Management</h2>
        </div>
        <p className="mt-1 text-sm text-gray-600">
          Control who can access the AI agent interface (Model Context Protocol).
        </p>
      </div>

      <div className="p-6 space-y-4">
        {/* Notifications */}
        {successMessage && (
          <div className="bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
            <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
            <span className="text-green-800 font-medium">{successMessage}</span>
          </div>
        )}
        {error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
            <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
            <span className="text-red-800">{error}</span>
          </div>
        )}

        {/* Policy Selector */}
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
          <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
            <div className="text-sm text-blue-800">
              <p className="font-medium">Access Policy</p>
              <p className="mt-1">{POLICY_DESCRIPTIONS[policy]}</p>
              {policy === "WhitelistOnly" && (
                <p className="mt-1">Global Admins always have access, no separate entry needed.</p>
              )}
            </div>
            <select
              value={policy}
              onChange={(e) => handlePolicyChange(e.target.value as McpPolicy)}
              disabled={savingPolicy}
              className="px-3 py-2 border border-blue-300 rounded-lg text-sm text-blue-900 bg-white focus:outline-none focus:ring-2 focus:ring-purple-500 disabled:opacity-50 transition-colors"
            >
              <option value="Disabled">{POLICY_LABELS.Disabled}</option>
              <option value="WhitelistOnly">{POLICY_LABELS.WhitelistOnly}</option>
              <option value="AllMembers">{POLICY_LABELS.AllMembers}</option>
            </select>
          </div>
        </div>

        {/* User list only visible when WhitelistOnly */}
        {policy === "WhitelistOnly" && (
          <>
            {/* Add user form */}
            <div className="flex flex-wrap gap-2">
              <input
                type="email"
                value={newEmail}
                onChange={(e) => setNewEmail(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    handleAddUser();
                  }
                }}
                placeholder="user@domain.com"
                autoComplete="off"
                className="flex-1 min-w-0 px-4 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
              />
              <button
                onClick={handleAddUser}
                disabled={adding || !newEmail.trim()}
                className="px-6 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                {adding ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" />
                    <span>Adding...</span>
                  </>
                ) : (
                  <>
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                    </svg>
                    <span>Add</span>
                  </>
                )}
              </button>
            </div>

            {/* Search */}
            {users.length > 3 && (
              <div className="relative">
                <input
                  type="text"
                  value={searchQuery}
                  onChange={(e) => {
                    setSearchQuery(e.target.value);
                    setCurrentPage(0);
                  }}
                  placeholder="Search by email..."
                  autoComplete="off"
                  className="w-full px-4 py-2 pl-10 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                />
                <svg className="absolute left-3 top-2.5 w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
              </div>
            )}

            {/* Loading */}
            {loading && (
              <div className="flex items-center justify-center py-8">
                <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-purple-600" />
                <span className="ml-3 text-sm text-gray-500">Loading MCP users...</span>
              </div>
            )}

            {/* User list */}
            {!loading && paginatedUsers.length === 0 && (
              <p className="text-sm text-gray-500 py-4 text-center">
                {searchQuery ? "No users match your search." : "No MCP users added yet. Add a user above to grant MCP access."}
              </p>
            )}

            {!loading && (
              <div className="space-y-2">
                {paginatedUsers.map((mcpUser) => (
                  <div
                    key={mcpUser.upn}
                    className={`p-3 border rounded-lg ${
                      mcpUser.isEnabled ? "bg-gray-50 border-gray-200" : "bg-gray-100 border-gray-300"
                    }`}
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div className="flex-1 min-w-0">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <span className="font-medium text-gray-900 truncate">{mcpUser.upn}</span>
                          {mcpUser.isEnabled ? (
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
                              Active
                            </span>
                          ) : (
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                              Disabled
                            </span>
                          )}
                        </div>
                        <div className="text-xs text-gray-500 mt-1">
                          Added {new Date(mcpUser.addedAt).toLocaleDateString()} by {mcpUser.addedBy}
                        </div>
                      </div>

                      <div className="flex flex-wrap items-center gap-2">
                        {/* Usage Plan Dropdown */}
                        {planTiers.length > 0 && (
                          <select
                            value={mcpUser.usagePlan || ""}
                            onChange={(e) => handleSetUsagePlan(mcpUser.upn, e.target.value)}
                            disabled={changingPlanUpn === mcpUser.upn}
                            className="px-2 py-1 text-sm border border-gray-300 rounded-lg bg-white focus:outline-none focus:ring-2 focus:ring-purple-500 disabled:opacity-50 transition-colors"
                          >
                            <option value="">Inherit (tenant default)</option>
                            {planTiers.map((tier) => (
                              <option key={tier.name} value={tier.name}>
                                {tier.name}
                              </option>
                            ))}
                          </select>
                        )}
                        <button
                          onClick={() => handleToggleUser(mcpUser.upn, mcpUser.isEnabled)}
                          disabled={togglingUpn === mcpUser.upn}
                          className="px-3 py-1 text-sm border border-gray-300 rounded-lg hover:bg-gray-100 disabled:opacity-50 transition-colors"
                        >
                          {togglingUpn === mcpUser.upn ? "..." : mcpUser.isEnabled ? "Disable" : "Enable"}
                        </button>
                        <button
                          onClick={() => handleRemoveUser(mcpUser.upn)}
                          disabled={removingUpn === mcpUser.upn}
                          className="px-3 py-1 text-sm text-red-600 border border-red-200 rounded-lg hover:bg-red-50 disabled:opacity-50 transition-colors"
                        >
                          {removingUpn === mcpUser.upn ? "..." : "Remove"}
                        </button>
                      </div>
                    </div>
                  </div>
                ))}
              </div>
            )}

            {/* Pagination */}
            {totalPages > 1 && (
              <div className="flex items-center justify-between mt-4 pt-4 border-t border-gray-200">
                <button
                  onClick={() => setCurrentPage((prev) => Math.max(0, prev - 1))}
                  disabled={currentPage === 0}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Previous
                </button>
                <span className="text-sm text-gray-600">
                  Page {currentPage + 1} of {totalPages} ({filteredUsers.length} user{filteredUsers.length !== 1 ? "s" : ""})
                </span>
                <button
                  onClick={() => setCurrentPage((prev) => Math.min(totalPages - 1, prev + 1))}
                  disabled={currentPage >= totalPages - 1}
                  className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Next
                </button>
              </div>
            )}
          </>
        )}

        {/* Message for other policies */}
        {policy === "Disabled" && (
          <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-4 text-sm text-yellow-800">
            MCP access is currently disabled. Change the policy in Global Config to enable it.
          </div>
        )}
        {policy === "AllMembers" && (
          <div className="bg-green-50 border border-green-200 rounded-lg p-4 text-sm text-green-800">
            All authenticated users can access the MCP server. No whitelist management needed.
          </div>
        )}
      </div>
    </div>
  );
}
