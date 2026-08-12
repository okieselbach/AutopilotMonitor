"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";

interface TenantAdmin {
  tenantId: string;
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
  role: string | null;
  canManageBootstrapTokens: boolean;
}

interface TenantAdminSectionProps {
  tenantId: string;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function TenantAdminSection({
  tenantId,
  getAccessToken,
  setError,
  setSuccessMessage,
}: TenantAdminSectionProps) {
  // Read-only Global Readers may view tenant members but not add/remove/toggle them.
  const canMutate = useCanMutatePlatform();
  const [tenantAdmins, setTenantAdmins] = useState<TenantAdmin[]>([]);
  const [loadingAdmins, setLoadingAdmins] = useState(false);
  const [newAdminEmail, setNewAdminEmail] = useState("");
  const [newMemberRole, setNewMemberRole] = useState<string>("Admin");
  const [addingAdmin, setAddingAdmin] = useState(false);
  const [removingAdmin, setRemovingAdmin] = useState<string | null>(null);
  const [togglingAdmin, setTogglingAdmin] = useState<string | null>(null);
  const [currentAdminPage, setCurrentAdminPage] = useState(0);
  const [adminSearchQuery, setAdminSearchQuery] = useState("");
  const adminsPerPage = 5;

  // Fetch tenant admins when tenantId changes
  const fetchTenantAdmins = async (tid: string) => {
    try {
      setLoadingAdmins(true);
      setCurrentAdminPage(0); // Reset to first page when loading new tenant

      const response = await authenticatedFetch(api.tenants.admins(tid), getAccessToken);

      if (!response.ok) {
        throw new Error(`Failed to load admins: ${response.statusText}`);
      }

      const data: TenantAdmin[] = await response.json();
      setTenantAdmins(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching tenant admins");
      } else {
        console.error("Error fetching tenant admins:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load tenant admins");
    } finally {
      setLoadingAdmins(false);
    }
  };

  useEffect(() => {
    const run = async () => {
      await fetchTenantAdmins(tenantId);
    };
    void run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId]);

  const handleAddTenantAdmin = async () => {
    if (!canMutate) return; // read-only Global Reader — also closes the Enter-key path past disabled buttons
    if (!newAdminEmail.trim()) return;

    try {
      setAddingAdmin(true);
      setError(null);

      const response = await authenticatedFetch(api.tenants.admins(tenantId), getAccessToken, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ upn: newAdminEmail.trim(), role: newMemberRole }),
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to add admin: ${response.statusText}`);
      }

      setSuccessMessage(`${newMemberRole} ${newAdminEmail} added successfully!`);
      setNewAdminEmail("");

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while adding tenant admin");
      } else {
        console.error("Error adding tenant admin:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to add admin");
    } finally {
      setAddingAdmin(false);
    }
  };

  const handleRemoveTenantAdmin = async (adminUpn: string) => {
    if (!canMutate) return; // read-only Global Reader
    if (!confirm(`Are you sure you want to remove ${adminUpn} as an admin?`)) {
      return;
    }

    try {
      setRemovingAdmin(adminUpn);
      setError(null);

      const response = await authenticatedFetch(api.tenants.admin(tenantId, adminUpn), getAccessToken, {
        method: "DELETE",
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to remove admin: ${response.statusText}`);
      }

      setSuccessMessage(`Admin ${adminUpn} removed successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while removing tenant admin");
      } else {
        console.error("Error removing tenant admin:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to remove admin");
    } finally {
      setRemovingAdmin(null);
    }
  };

  const handleToggleTenantAdmin = async (adminUpn: string, isEnabled: boolean) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setTogglingAdmin(adminUpn);
      setError(null);

      const action = isEnabled ? 'disable' : 'enable';
      const response = await authenticatedFetch(api.tenants.adminAction(tenantId, adminUpn, action), getAccessToken, {
        method: "PATCH",
      });

      if (!response.ok) {
        let errorData;
        try {
          errorData = await response.json();
        } catch {
          errorData = { error: `Failed to ${action} admin: ${response.statusText}` };
        }
        throw new Error(errorData.error || `Failed to ${action} admin: ${response.statusText}`);
      }

      setSuccessMessage(`Admin ${adminUpn} ${isEnabled ? 'disabled' : 'enabled'} successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while toggling tenant admin");
      } else {
        console.error("Error toggling tenant admin:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to toggle admin");
    } finally {
      setTogglingAdmin(null);
    }
  };

  const handleUpdatePermissions = async (adminUpn: string, role: string, canManageBootstrapTokens: boolean) => {
    if (!canMutate) return; // read-only Global Reader (consistency with the other mutation handlers)
    try {
      setTogglingAdmin(adminUpn);
      setError(null);

      const response = await authenticatedFetch(api.tenants.adminPermissions(tenantId, adminUpn), getAccessToken, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ role, canManageBootstrapTokens }),
      });

      if (!response.ok) {
        let errorData;
        try {
          errorData = await response.json();
        } catch {
          errorData = { error: `Failed to update permissions: ${response.statusText}` };
        }
        throw new Error(errorData.error || `Failed to update permissions: ${response.statusText}`);
      }

      setSuccessMessage(`Permissions for ${adminUpn} updated successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while updating member permissions");
      } else {
        console.error("Error updating member permissions:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to update permissions");
    } finally {
      setTogglingAdmin(null);
    }
  };

  // Admin Pagination with Search
  const filteredAdmins = tenantAdmins.filter(admin =>
    admin.upn.toLowerCase().includes(adminSearchQuery.toLowerCase())
  );
  const totalAdminPages = Math.ceil(filteredAdmins.length / adminsPerPage);
  const startAdminIndex = currentAdminPage * adminsPerPage;
  const endAdminIndex = startAdminIndex + adminsPerPage;
  const paginatedAdmins = filteredAdmins.slice(startAdminIndex, endAdminIndex);

  return (
    <div className="bg-purple-50 border border-purple-200 rounded-lg p-4">
      <div className="flex items-start space-x-3">
        <svg className="w-5 h-5 text-purple-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z" />
        </svg>
        <div className="flex-1">
          <p className="font-semibold text-purple-900 mb-2">Admin Users Management</p>

          {/* Current Admins List */}
          <div className="mb-3">
            <p className="text-sm text-purple-800 font-medium mb-2">
              Current Admins ({tenantAdmins.length}):
            </p>

            {/* Search Field */}
            <div className="mb-3">
              <div className="relative">
                <input
                  type="text"
                  name="admin-search-modal"
                  value={adminSearchQuery}
                  onChange={(e) => {
                    setAdminSearchQuery(e.target.value);
                    setCurrentAdminPage(0);
                  }}
                  placeholder="Search by email..."
                  autoComplete="off"
                  className="w-full px-3 py-2 pl-9 pr-9 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                />
                <svg className="absolute left-2.5 top-2.5 w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
                {adminSearchQuery && (
                  <button
                    onClick={() => {
                      setAdminSearchQuery("");
                      setCurrentAdminPage(0);
                    }}
                    className="absolute right-2.5 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                    title="Clear search"
                  >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                  </button>
                )}
              </div>
            </div>

            {loadingAdmins ? (
              <div className="text-sm text-purple-700">Loading admins...</div>
            ) : tenantAdmins.length === 0 ? (
              <div className="text-sm text-purple-700 italic">No admins configured yet</div>
            ) : filteredAdmins.length === 0 ? (
              <div className="text-sm text-purple-700 italic p-3 text-center bg-purple-50 rounded-lg">
                No admins match your search
              </div>
            ) : (
              <>
                <div className="space-y-2">
                  {paginatedAdmins.map((admin) => {
                    const effectiveRole = admin.role ?? "Admin";
                    return (
                    <div
                      key={admin.upn}
                      className={`p-2 bg-white border rounded text-sm ${
                        admin.isEnabled ? 'border-purple-200' : 'border-gray-300 bg-gray-50'
                      }`}
                    >
                      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div className="flex-1 min-w-0">
                        <div className="flex flex-wrap items-center gap-1.5">
                          <div className="font-medium text-gray-900 truncate">{admin.upn}</div>
                          <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${
                            (admin.role ?? "Admin") === "Admin"
                              ? "bg-purple-100 text-purple-800"
                              : (admin.role === "Operator")
                              ? "bg-blue-100 text-blue-800"
                              : "bg-gray-100 text-gray-700"
                          }`}>
                            {admin.role ?? "Admin"}
                          </span>
                          {!admin.isEnabled && (
                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                              Disabled
                            </span>
                          )}
                        </div>
                        <div className="text-xs text-gray-500">
                          Added {new Date(admin.addedDate).toLocaleDateString()} by {admin.addedBy}
                        </div>
                      </div>
                      <div className="flex flex-wrap items-center gap-2">
                        <select
                          value={effectiveRole}
                          onChange={(e) => handleUpdatePermissions(admin.upn, e.target.value, admin.canManageBootstrapTokens)}
                          disabled={!canMutate || togglingAdmin === admin.upn}
                          className="px-2 py-1 text-xs border border-gray-300 rounded bg-white text-gray-700 focus:outline-none focus:ring-1 focus:ring-purple-500 disabled:opacity-50"
                        >
                          <option value="Admin">Admin</option>
                          <option value="Operator">Operator</option>
                        </select>
                        <button
                          onClick={() => handleToggleTenantAdmin(admin.upn, admin.isEnabled)}
                          disabled={!canMutate || togglingAdmin === admin.upn}
                          className={`px-2 py-1 text-xs rounded hover:opacity-80 disabled:opacity-50 disabled:cursor-not-allowed ${
                            admin.isEnabled
                              ? 'bg-yellow-600 text-white hover:bg-yellow-700'
                              : 'bg-green-600 text-white hover:bg-green-700'
                          }`}
                        >
                          {togglingAdmin === admin.upn
                            ? "..."
                            : admin.isEnabled ? "Disable" : "Enable"}
                        </button>
                        <button
                          onClick={() => handleRemoveTenantAdmin(admin.upn)}
                          disabled={!canMutate || removingAdmin === admin.upn}
                          className="px-2 py-1 text-xs bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed"
                        >
                          {removingAdmin === admin.upn ? "..." : "Remove"}
                        </button>
                      </div>
                      </div>

                      {/* Bootstrap token permission toggle for Operators */}
                      {effectiveRole === "Operator" && (
                        <div className="mt-2 pt-2 border-t border-gray-200">
                          <label className="flex items-center space-x-2 cursor-pointer">
                            <input
                              type="checkbox"
                              checked={admin.canManageBootstrapTokens}
                              onChange={(e) => handleUpdatePermissions(admin.upn, "Operator", e.target.checked)}
                              disabled={!canMutate || togglingAdmin === admin.upn}
                              className="h-4 w-4 text-purple-600 rounded border-gray-300 focus:ring-purple-500 disabled:opacity-50"
                            />
                            <span className="text-sm text-gray-700">Can manage bootstrap tokens</span>
                          </label>
                        </div>
                      )}
                    </div>
                    );
                  })}
                </div>

                {/* Pagination */}
                {totalAdminPages > 1 && (
                  <div className="flex items-center justify-between pt-3 mt-3 border-t border-purple-200">
                    <button
                      onClick={() => setCurrentAdminPage(p => Math.max(0, p - 1))}
                      disabled={currentAdminPage === 0}
                      className="px-3 py-1 text-xs font-medium text-purple-700 bg-white border border-purple-300 rounded hover:bg-purple-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                    >
                      Previous
                    </button>
                    <span className="text-xs text-purple-700">
                      Page {currentAdminPage + 1} of {totalAdminPages} ({filteredAdmins.length} admin{filteredAdmins.length !== 1 ? 's' : ''})
                    </span>
                    <button
                      onClick={() => setCurrentAdminPage(p => Math.min(totalAdminPages - 1, p + 1))}
                      disabled={currentAdminPage >= totalAdminPages - 1}
                      className="px-3 py-1 text-xs font-medium text-purple-700 bg-white border border-purple-300 rounded hover:bg-purple-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                    >
                      Next
                    </button>
                  </div>
                )}
              </>
            )}
          </div>

          {/* Add New Admin */}
          <div>
            <p className="text-sm text-purple-800 font-medium mb-2">Add New Admin:</p>
            <div className="flex space-x-2">
              <input
                type="email"
                name="modal-new-admin-email"
                id="modal-add-admin-email-input"
                value={newAdminEmail}
                onChange={(e) => setNewAdminEmail(e.target.value)}
                placeholder="admin@tenant.com"
                autoComplete="off"
                className="flex-1 px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-green-500 focus:border-green-500 transition-colors"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    handleAddTenantAdmin();
                  }
                }}
              />
              <select
                value={newMemberRole}
                onChange={(e) => setNewMemberRole(e.target.value)}
                className="px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-700 bg-white focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
              >
                <option value="Admin">Admin</option>
                <option value="Operator">Operator</option>
              </select>
              <button
                onClick={() => handleAddTenantAdmin()}
                disabled={!canMutate || addingAdmin || !newAdminEmail.trim()}
                className="px-4 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm"
              >
                {addingAdmin ? "Adding..." : "Add"}
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
