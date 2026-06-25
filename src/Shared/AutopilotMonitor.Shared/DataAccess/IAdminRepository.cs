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
