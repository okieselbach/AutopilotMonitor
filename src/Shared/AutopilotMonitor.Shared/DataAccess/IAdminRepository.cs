using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for admin and tenant member management.
    /// Covers: GlobalAdmins, TenantAdmins, McpUsers, DelegatedAdmins tables.
    /// </summary>
    public interface IAdminRepository
    {
        // --- Global Admins ---
        Task<bool> IsGlobalAdminAsync(string upn);
        /// <summary>
        /// Resolves the platform role for a UPN: "GlobalAdmin", "GlobalReader", or null when there is no
        /// enabled GlobalAdmins row. Empty/missing Role on an existing enabled row ⇒ "GlobalAdmin" (back-compat).
        /// </summary>
        Task<string?> GetGlobalRoleAsync(string upn);
        Task<List<GlobalAdminEntry>> GetAllGlobalAdminsAsync();
        Task<bool> AddGlobalAdminAsync(string upn, string addedBy);
        Task<bool> RemoveGlobalAdminAsync(string upn);
        Task<bool> DisableGlobalAdminAsync(string upn);

        // --- MCP Users ---
        Task<bool> IsMcpUserAsync(string upn);
        Task<McpUserEntry?> GetMcpUserAsync(string upn);
        Task<List<McpUserEntry>> GetAllMcpUsersAsync();
        Task<bool> AddMcpUserAsync(string upn, string addedBy);
        Task<bool> RemoveMcpUserAsync(string upn);
        Task<bool> SetMcpUserEnabledAsync(string upn, bool isEnabled);
        Task<bool> SetMcpUserUsagePlanAsync(string upn, string? usagePlan);

        // --- Delegated Admins (scoped-global / "MSP mode") ---
        /// <summary>Every assignment row across all delegated admins (full-table scan — the management UI lists
        /// all grants; the table holds only admin assignments, so this stays small). Not a hot path.</summary>
        Task<List<DelegatedAdminEntry>> GetAllDelegatedAdminsAsync();
        /// <summary>All assignment rows for one delegated-admin UPN (PK scan), regardless of status/enabled.
        /// Callers that need only effective scope filter on Status==Active &amp;&amp; IsEnabled.</summary>
        Task<List<DelegatedAdminEntry>> GetDelegatedTenantsAsync(string upn);
        /// <summary>All assignment rows targeting one tenant (cross-partition RowKey scan — admin UI, not hot path).
        /// Powers the customer's "who manages my tenant?" view including pending requests.</summary>
        Task<List<DelegatedAdminEntry>> GetDelegatedAssigneesAsync(string tenantId);
        Task<DelegatedAdminEntry?> GetDelegatedAdminAsync(string upn, string tenantId);
        /// <summary>Creates or replaces an assignment row (upsert on PK=upn, RK=tenantId).</summary>
        Task<bool> UpsertDelegatedAdminAsync(string upn, string tenantId, string role, string status, string source, string grantedBy);
        Task<bool> SetDelegatedAdminStatusAsync(string upn, string tenantId, string status);
        Task<bool> SetDelegatedAdminEnabledAsync(string upn, string tenantId, bool isEnabled);
        Task<bool> RemoveDelegatedAdminAsync(string upn, string tenantId);

        // --- Tenant Groups (app-internal tenant bundles for delegated admins / "MSP mode") ---
        /// <summary>Creates a group (meta row) and returns the generated groupId.</summary>
        Task<string> CreateTenantGroupAsync(string name, string createdBy);
        /// <summary>Renames a group (meta row). Returns false if the group does not exist.</summary>
        Task<bool> RenameTenantGroupAsync(string groupId, string name);
        /// <summary>Deletes a group: all rows in its partition (meta + membership) AND every UPN
        /// assignment referencing it (cross-partition RowKey scan of the assignments table).</summary>
        Task<bool> DeleteTenantGroupAsync(string groupId);
        /// <summary>All groups with their tenant members + assignee counts — for the management UI (not hot path).</summary>
        Task<List<TenantGroup>> GetAllTenantGroupsAsync();
        Task<TenantGroup?> GetTenantGroupAsync(string groupId);
        Task<bool> AddTenantToGroupAsync(string groupId, string tenantId);
        Task<bool> RemoveTenantFromGroupAsync(string groupId, string tenantId);
        /// <summary>Tenant IDs (lowercase) in a group, excluding the meta row (HOT PATH — PartitionKey scan).</summary>
        Task<List<string>> GetGroupTenantsAsync(string groupId);
        /// <summary>Creates or replaces a UPN→group assignment.</summary>
        Task<bool> AssignGroupAsync(string upn, string groupId, string role, bool isEnabled, string assignedBy);
        Task<bool> UnassignGroupAsync(string upn, string groupId);
        /// <summary>All group assignments for one UPN (HOT PATH — PartitionKey point-scan; scope resolution).</summary>
        Task<List<TenantGroupAssignment>> GetGroupAssignmentsForUpnAsync(string upn);
        /// <summary>All UPNs assigned to one group (cross-partition RowKey scan — management UI, not hot path).</summary>
        Task<List<TenantGroupAssignment>> GetGroupAssigneesAsync(string groupId);

        // --- Tenant Members ---
        Task<List<TenantMember>> GetTenantMembersAsync(string tenantId);
        Task<bool> AddTenantMemberAsync(string tenantId, string upn, string addedBy, string role, bool canManageBootstrapTokens = false);
        Task<bool> RemoveTenantMemberAsync(string tenantId, string upn);
        Task<bool> UpdateMemberPermissionsAsync(string tenantId, string upn, string role, bool canManageBootstrapTokens);
        Task<bool> SetTenantMemberEnabledAsync(string tenantId, string upn, bool isEnabled);
        Task<TenantMember?> GetTenantMemberAsync(string tenantId, string upn);
        Task<bool> IsTenantAdminAsync(string tenantId, string upn);
        Task<bool> IsTenantMemberAsync(string tenantId, string upn);
    }

    public class GlobalAdminEntry
    {
        public string Upn { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
        /// <summary>Platform role: "GlobalAdmin" (default) or "GlobalReader". Empty ⇒ GlobalAdmin (back-compat).</summary>
        public string Role { get; set; } = string.Empty;
    }

    /// <summary>
    /// One delegated-admin assignment: UPN X may access tenant Y at role Role. The "scoped global" tier
    /// (subset of tenants) between a single-tenant member and a platform GlobalAdmin. Surfaced as "MSP mode".
    /// </summary>
    public class DelegatedAdminEntry
    {
        public string Upn { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        /// <summary>Constants.DelegatedRoles: "DelegatedReader" (default) or "DelegatedAdmin".</summary>
        public string Role { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        /// <summary>Constants.DelegatedStatus: "Active" / "PendingApproval" / "Revoked". Only Active confers scope.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>Constants.DelegatedSource: "OperatorGranted" / "CustomerDelegated".</summary>
        public string Source { get; set; } = string.Empty;
        public DateTime GrantedAt { get; set; }
        public string GrantedBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// A Tenant Group: an app-internal named bundle of tenants. A delegated admin assigned to the
    /// group (see <see cref="TenantGroupAssignment"/>) gains read scope to every tenant in
    /// <see cref="TenantIds"/>. Adding a tenant to the group grants it to all assignees at once.
    /// </summary>
    public class TenantGroup
    {
        public string GroupId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        /// <summary>Tenant IDs (lowercase) in this group.</summary>
        public List<string> TenantIds { get; set; } = new();
        /// <summary>Number of UPNs assigned to this group (== <see cref="Assignees"/>.Count).</summary>
        public int AssigneeCount { get; set; }
        /// <summary>The UPNs assigned to this group (for the management UI).</summary>
        public List<TenantGroupAssignment> Assignees { get; set; } = new();
    }

    /// <summary>One UPN→group assignment. PK=UPN, RK=groupId in storage.</summary>
    public class TenantGroupAssignment
    {
        public string Upn { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        /// <summary>Constants.DelegatedRoles: "DelegatedReader" (default) or "DelegatedAdmin".</summary>
        public string Role { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public string AssignedBy { get; set; } = string.Empty;
        public DateTime AssignedAt { get; set; }
    }

    public class McpUserEntry
    {
        public string Upn { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
        public string? UsagePlan { get; set; }
    }

    public class TenantMember
    {
        public string Upn { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Role { get; set; } = "Admin";
        public bool IsEnabled { get; set; } = true;
        public bool CanManageBootstrapTokens { get; set; }
        public DateTime AddedAt { get; set; }
        public string AddedBy { get; set; } = string.Empty;
    }

}
