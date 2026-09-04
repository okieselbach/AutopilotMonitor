"use client";

import { TenantAdmin } from "../types";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { DOCS_PATHS } from "@/lib/docsPaths";
import { isApplicationKey, looksLikeGuid, principalLabel, type MemberKind } from "@/utils/principalKeys";

interface AdminManagementSectionProps {
  admins: TenantAdmin[];
  loadingAdmins: boolean;
  newAdminEmail: string;
  setNewAdminEmail: (value: string) => void;
  newMemberRole: string;
  setNewMemberRole: (value: string) => void;
  newMemberKind: MemberKind;
  setNewMemberKind: (value: MemberKind) => void;
  addingAdmin: boolean;
  removingAdmin: string | null;
  togglingAdmin: string | null;
  adminSearchQuery: string;
  setAdminSearchQuery: (value: string) => void;
  currentAdminPage: number;
  setCurrentAdminPage: (value: number | ((prev: number) => number)) => void;
  user: { upn?: string } | null;
  onAddAdmin: () => void;
  onRemoveAdmin: (upn: string) => void;
  onToggleAdmin: (upn: string, isEnabled: boolean) => void;
  onUpdatePermissions: (upn: string, role: string, canManageBootstrapTokens: boolean) => void;
}

function getRoleBadge(role: string | null | undefined) {
  const effectiveRole = role ?? "Admin";
  switch (effectiveRole) {
    case "Admin":
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-800">
          Admin
        </span>
      );
    case "Operator":
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-800">
          Operator
        </span>
      );
    case "Viewer":
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-800">
          Viewer
        </span>
      );
    default:
      return (
        <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600">
          {effectiveRole}
        </span>
      );
  }
}

export default function AdminManagementSection({
  admins,
  loadingAdmins,
  newAdminEmail,
  setNewAdminEmail,
  newMemberRole,
  setNewMemberRole,
  newMemberKind,
  setNewMemberKind,
  addingAdmin,
  removingAdmin,
  togglingAdmin,
  adminSearchQuery,
  setAdminSearchQuery,
  currentAdminPage,
  setCurrentAdminPage,
  user,
  onAddAdmin,
  onRemoveAdmin,
  onToggleAdmin,
  onUpdatePermissions,
}: AdminManagementSectionProps) {
  const addingApplication = newMemberKind === "application";
  return (
    <div className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="purple"
        iconPath="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z"
        title="Access Management"
        subtitle="Manage team members and their roles for this tenant"
        docsPath={DOCS_PATHS.accessManagement}
      />
      <div className="p-6 space-y-4">
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
          <div className="flex items-start space-x-3">
            <svg className="w-5 h-5 text-blue-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <div className="text-sm text-blue-800">
              <p className="font-medium">About Roles</p>
              <p className="mt-1">
                <strong>Member</strong> — No role assigned. Sees only the Progress Portal for their own enrollments.
              </p>
              <p className="mt-1">
                <strong>Viewer</strong> — Read-only access to everything: sessions, rules, settings, and reports. Cannot change anything or trigger actions.
              </p>
              <p className="mt-1">
                <strong>Operator</strong> — Day-to-day operations: dashboard, sessions, and monitoring. Can optionally manage bootstrap tokens if permitted.
              </p>
              <p className="mt-1">
                <strong>Admin</strong> — Full management: all tenant configuration, sessions, diagnostics, and settings.
              </p>
              <p className="mt-2">
                <strong>Your email:</strong> {user?.upn}
              </p>
            </div>
          </div>
        </div>

        {/* Current Members List */}
        <div>
          <label className="block mb-2">
            <span className="text-gray-700 font-medium">Current Team Members</span>
            {loadingAdmins && (
              <span className="ml-2 text-sm text-gray-500">(Loading...)</span>
            )}
          </label>

          {/* Search Field */}
          <div className="mb-3">
            <div className="relative">
              <input
                type="text"
                name="admin-search-field"
                value={adminSearchQuery}
                onChange={(e) => {
                  setAdminSearchQuery(e.target.value);
                  setCurrentAdminPage(0);
                }}
                placeholder="Search by email..."
                autoComplete="off"
                className="w-full px-4 py-2 pl-10 pr-10 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
              />
              <svg className="absolute left-3 top-2.5 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
              {adminSearchQuery && (
                <button
                  onClick={() => {
                    setAdminSearchQuery("");
                    setCurrentAdminPage(0);
                  }}
                  className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                  title="Clear search"
                >
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              )}
            </div>
          </div>

          {admins.length === 0 && !loadingAdmins ? (
            <div className="text-sm text-gray-500 italic">No members found</div>
          ) : (
            <>
              {/* Filtered and Paginated Member List */}
              {(() => {
                const filteredAdmins = admins.filter(admin =>
                  principalLabel(admin.upn).toLowerCase().includes(adminSearchQuery.toLowerCase())
                );

                if (filteredAdmins.length === 0) {
                  return (
                    <div className="text-sm text-gray-500 italic p-4 text-center bg-gray-50 rounded-lg">
                      No members match your search
                    </div>
                  );
                }

                const adminsPerPage = 3;
                const totalAdminPages = Math.ceil(filteredAdmins.length / adminsPerPage);
                const startAdminIndex = currentAdminPage * adminsPerPage;
                const endAdminIndex = startAdminIndex + adminsPerPage;
                const paginatedAdmins = filteredAdmins.slice(startAdminIndex, endAdminIndex);

                return (
                  <>
                    <div className="space-y-2">
                      {paginatedAdmins.map((admin) => {
                        const isApplication = isApplicationKey(admin.upn);
                        const effectiveRole = isApplication ? "Viewer" : (admin.role ?? "Admin");
                        const isCurrentUser = admin.upn.toLowerCase() === user?.upn?.toLowerCase();

                        return (
                          <div
                            key={admin.upn}
                            className={`p-3 border rounded-lg ${
                              admin.isEnabled
                                ? "bg-gray-50 border-gray-200"
                                : "bg-gray-100 border-gray-300"
                            }`}
                          >
                            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                              <div className="flex-1 min-w-0">
                                <div className="flex flex-wrap items-center gap-1.5">
                                  <div className="font-medium text-gray-900 truncate">{principalLabel(admin.upn)}</div>
                                  {isApplication && (
                                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-700" title="Service principal — an application calling with an app-only token; read-only">
                                      App
                                    </span>
                                  )}
                                  {getRoleBadge(effectiveRole)}
                                  {!admin.isEnabled && (
                                    <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                                      Disabled
                                    </span>
                                  )}
                                </div>
                                <div className="text-xs text-gray-500 mt-1">
                                  Added {new Date(admin.addedDate).toLocaleDateString()} by {admin.addedBy}
                                </div>
                              </div>
                              <div className="flex flex-wrap items-center gap-2">
                                {isCurrentUser ? (
                                  <span className="text-sm text-blue-600 font-medium">(You)</span>
                                ) : (
                                  <>
                                    {/* Role change dropdown */}
                                    <select
                                      value={effectiveRole}
                                      onChange={(e) => onUpdatePermissions(admin.upn, e.target.value, admin.canManageBootstrapTokens)}
                                      disabled={isApplication || togglingAdmin === admin.upn}
                                      title={isApplication ? "A service principal is always read-only (Viewer)" : undefined}
                                      className="px-2 py-1 text-sm border border-gray-300 rounded bg-white text-gray-700 focus:outline-none focus:ring-1 focus:ring-purple-500 disabled:opacity-50"
                                    >
                                      <option value="Admin">Admin</option>
                                      <option value="Operator">Operator</option>
                                      <option value="Viewer">Viewer</option>
                                    </select>
                                    <button
                                      onClick={() => onToggleAdmin(admin.upn, admin.isEnabled)}
                                      disabled={togglingAdmin === admin.upn}
                                      className={`px-3 py-1 text-sm text-white rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                        admin.isEnabled
                                          ? "bg-yellow-600 hover:bg-yellow-700"
                                          : "bg-green-600 hover:bg-green-700"
                                      }`}
                                    >
                                      {togglingAdmin === admin.upn
                                        ? "..."
                                        : admin.isEnabled
                                        ? "Disable"
                                        : "Enable"}
                                    </button>
                                    <button
                                      onClick={() => onRemoveAdmin(admin.upn)}
                                      disabled={removingAdmin === admin.upn}
                                      className="px-3 py-1 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                    >
                                      {removingAdmin === admin.upn ? "Removing..." : "Remove"}
                                    </button>
                                  </>
                                )}
                              </div>
                            </div>

                            {/* Bootstrap token permission toggle for Operators */}
                            {effectiveRole === "Operator" && !isCurrentUser && (
                              <div className="mt-2 pt-2 border-t border-gray-200">
                                <label className="flex items-center space-x-2 cursor-pointer">
                                  <input
                                    type="checkbox"
                                    checked={admin.canManageBootstrapTokens}
                                    onChange={(e) => onUpdatePermissions(admin.upn, "Operator", e.target.checked)}
                                    disabled={togglingAdmin === admin.upn}
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

                    {/* Pagination Controls */}
                    {totalAdminPages > 1 && (
                      <div className="flex items-center justify-between mt-4 pt-4 border-t border-gray-200">
                        <button
                          onClick={() => setCurrentAdminPage(prev => Math.max(0, prev - 1))}
                          disabled={currentAdminPage === 0}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Previous
                        </button>
                        <span className="text-sm text-gray-600">
                          Page {currentAdminPage + 1} of {totalAdminPages} ({filteredAdmins.length} member{filteredAdmins.length !== 1 ? 's' : ''})
                        </span>
                        <button
                          onClick={() => setCurrentAdminPage(prev => Math.min(totalAdminPages - 1, prev + 1))}
                          disabled={currentAdminPage >= totalAdminPages - 1}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Next
                        </button>
                      </div>
                    )}
                  </>
                );
              })()}
            </>
          )}
        </div>

        {/* Add New Member */}
        <div>
          <label className="block mb-2">
            <span className="text-gray-700 font-medium">Add New Team Member</span>
            <div className="flex flex-wrap items-center gap-2 mb-2">
              <p className="text-sm text-gray-500">
                {addingApplication
                  ? "Enter the application (client) ID of a service principal in your tenant. It is always read-only (Viewer) and must hold the access_as_application permission for Autopilot Monitor, granted by admin consent in your Entra tenant."
                  : "Enter the user email (UPN) and select a role to grant access."}
              </p>
            </div>
            <div className="flex flex-wrap gap-2">
              <div className="inline-flex rounded-lg border border-gray-300 overflow-hidden text-sm" role="group" aria-label="Member type">
                {([["user", "User"], ["application", "Service principal"]] as const).map(([kind, label]) => (
                  <button
                    key={kind}
                    type="button"
                    onClick={() => { setNewMemberKind(kind); setNewAdminEmail(""); }}
                    className={`px-3 py-2 ${newMemberKind === kind ? "bg-purple-600 text-white" : "bg-white text-gray-700 hover:bg-gray-50"}`}
                  >
                    {label}
                  </button>
                ))}
              </div>
              <input
                type={addingApplication ? "text" : "email"}
                name="new-admin-email"
                id="add-new-admin-email-input"
                value={newAdminEmail}
                onChange={(e) => setNewAdminEmail(e.target.value)}
                placeholder={addingApplication ? "Application (client) ID, e.g. 00000000-0000-0000-0000-000000000000" : "user@tenant.com"}
                autoComplete="off"
                className="flex-1 min-w-0 px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    onAddAdmin();
                  }
                }}
              />
              <select
                value={addingApplication ? "Viewer" : newMemberRole}
                onChange={(e) => setNewMemberRole(e.target.value)}
                disabled={addingApplication}
                title={addingApplication ? "A service principal is always read-only (Viewer)" : undefined}
                className="px-3 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors disabled:opacity-50"
              >
                <option value="Admin">Admin</option>
                <option value="Operator">Operator</option>
                <option value="Viewer">Viewer</option>
              </select>
              <button
                onClick={onAddAdmin}
                disabled={addingAdmin || !(addingApplication ? looksLikeGuid(newAdminEmail) : newAdminEmail.trim())}
                className="px-6 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                {addingAdmin ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Adding...</span>
                  </>
                ) : (
                  <>
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                    </svg>
                    <span>Add</span>
                  </>
                )}
              </button>
            </div>
          </label>
        </div>

        <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3">
          <div className="flex items-start space-x-2">
            <svg className="w-5 h-5 text-yellow-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <p className="text-sm text-yellow-800">
              <strong>Important:</strong> Make sure to keep at least one Admin in the list to maintain full access!
              The first user to log in was automatically made an admin.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
