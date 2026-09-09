using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for the <c>RuleSubmissions</c> table (one partition, newest-first RowKeys).
    /// Expected volume is small — a submission is a rare, deliberate act — so the tenant view and
    /// the point lookup are property filters over the partition rather than a second key layout.
    /// </summary>
    public interface IRuleSubmissionRepository
    {
        Task<bool> AddAsync(RuleSubmission submission);

        /// <summary>Point lookup by id across all tenants; null when unknown.</summary>
        Task<RuleSubmission?> GetAsync(string submissionId);

        /// <summary>Every submission of one tenant, newest first.</summary>
        Task<List<RuleSubmission>> GetForTenantAsync(string tenantId);

        /// <summary>One page newest-first, optionally filtered by tenant and stored status.</summary>
        Task<RawPage<RuleSubmission>> GetPageAsync(string? tenantId, string? status, int pageSize, string? continuation);

        /// <summary>Replaces the row's mutable review columns; false when the row is gone.</summary>
        Task<bool> UpdateAsync(RuleSubmission submission);

        /// <summary>The published ids reserved by approved submissions — the third set the id suggestion excludes.</summary>
        Task<HashSet<string>> GetReservedPublishedRuleIdsAsync();
    }
}
