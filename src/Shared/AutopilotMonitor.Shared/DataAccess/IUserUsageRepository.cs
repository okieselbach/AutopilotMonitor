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
        /// Gets usage records for all users belonging to a tenant within an optional date range.
        /// </summary>
        Task<List<UserUsageRecord>> GetUsageByTenantAsync(string tenantId, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Gets aggregated daily usage summaries, optionally filtered by tenant.
        /// </summary>
        Task<List<UserUsageDailySummary>> GetDailySummaryAsync(string? tenantId = null, string? dateFrom = null, string? dateTo = null);

        /// <summary>
        /// Deletes all usage records older than the specified date (yyyyMMdd format).
        /// Returns the number of records deleted.
        /// </summary>
        Task<int> DeleteRecordsOlderThanAsync(string dateCutoff);

        /// <summary>
        /// Increments the tenant-wide MCP counter for (tenant, user, today) — the organization-wide
        /// quota's counter, kept per user so the tenant partition has no hot row. <paramref name="tenantId"/>
        /// is the CHARGED tenant: the caller's own tenant, or — for a delegated (MSP) read — the managed
        /// tenant whose plan governs the request. <paramref name="homeTenantId"/> is the caller's home tenant
        /// when it differs from the charged tenant (empty/null for the tenant's own members), so the charged
        /// tenant's admins can tell delegated consumption from their own.
        /// </summary>
        Task IncrementTenantUsageAsync(string tenantId, string userId, string? userPrincipalName, string? homeTenantId);

        /// <summary>Tenant-wide MCP counters (one row per user and day) within an optional yyyyMMdd range.</summary>
        Task<List<TenantUsageRecord>> GetTenantUsageAsync(string tenantId, string? dateFrom = null, string? dateTo = null);

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
        /// <summary>The caller's HOME tenant when the row was charged by a delegated (MSP) read; empty for
        /// the tenant's own members (and for rows written before the column existed).</summary>
        public string HomeTenantId { get; set; } = string.Empty;
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

    /// <summary>
    /// Aggregated daily usage summary across endpoints (and optionally users).
    /// </summary>
    public class UserUsageDailySummary
    {
        public string Date { get; set; } = string.Empty;
        public string? TenantId { get; set; }
        public long TotalRequests { get; set; }
        public int UniqueUsers { get; set; }
        public int UniqueEndpoints { get; set; }
    }
}
