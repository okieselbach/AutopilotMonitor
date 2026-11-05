using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for tracking per-user API usage over time.
    /// Stores per-user, per-day, per-endpoint request counts.
    /// </summary>
    public interface IUserUsageRepository
    {
        /// <summary>
        /// Increments the usage counter for a given user/endpoint/day combination.
        /// Uses optimistic concurrency with retry on conflict.
        /// </summary>
        Task IncrementUsageAsync(string userId, string userPrincipalName, string tenantId, string endpoint);

        /// <summary>
        /// Gets usage records for a specific user within an optional date range.
        /// </summary>
        Task<List<UserUsageRecord>> GetUsageByUserAsync(string userId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Deletes all usage records older than the specified date (yyyyMMdd format).
        /// Returns the number of records deleted.
        /// </summary>
        Task<int> DeleteRecordsOlderThanAsync(string dateCutoff);

        /// <summary>
        /// Increments the tenant-wide MCP counter for (tenant, user, today) — the organization-wide
        /// quota's counter, kept per user so the tenant partition has no hot row. <paramref name="tenantId"/>
        /// is always the caller's HOME tenant: every MCP request, a delegated (MSP) read included, draws on
        /// the caller's own organization budget, never on a managed tenant's.
        /// </summary>
        Task IncrementTenantUsageAsync(string tenantId, string userId, string? userPrincipalName);

        /// <summary>Tenant-wide MCP counters (one row per user and day) within an optional yyyyMMdd range.</summary>
        Task<List<TenantUsageRecord>> GetTenantUsageAsync(string tenantId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// The tenant-wide MCP counters of EVERY tenant within a yyyyMMdd range — the source of the all-tenant
        /// usage overview. One row per account and day (no endpoint dimension), so this reads a fraction of the
        /// per-user log for the same window.
        /// </summary>
        Task<List<TenantUsageRecord>> GetAllTenantUsageAsync(string dateFrom, string dateTo);

        /// <summary>Deletes tenant-wide MCP counters older than the specified date (yyyyMMdd). Returns the count deleted.</summary>
        Task<int> DeleteTenantRecordsOlderThanAsync(string dateCutoff);
    }

    /// <summary>One tenant-wide MCP counter row: one tenant, one user, one day.</summary>
    public class TenantUsageRecord
    {
        public string TenantId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        /// <summary>UPN as last seen; empty on rows written before the column existed.</summary>
        public string UserPrincipalName { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public long RequestCount { get; set; }
        public DateTime? LastRequestAt { get; set; }
    }

    /// <summary>
    /// A single usage record: one user, one day, one endpoint.
    /// </summary>
    public class UserUsageRecord
    {
        public string UserId { get; set; } = string.Empty;
        public string UserPrincipalName { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public long RequestCount { get; set; }
        public DateTime LastRequestAt { get; set; }
    }
}
