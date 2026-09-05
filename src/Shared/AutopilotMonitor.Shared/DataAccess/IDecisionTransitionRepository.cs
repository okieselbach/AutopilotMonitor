using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for the <c>DecisionTransitions</c> primary table (Plan §M5). Writes projected
    /// <see cref="DecisionTransitionRecord"/>s derived from agent Journal entries.
    /// <para>
    /// Writes are idempotent via <c>(PartitionKey, RowKey)</c>: a retried batch replays without
    /// duplicating rows. Records within a single batch must share the same (TenantId, SessionId).
    /// </para>
    /// </summary>
    public interface IDecisionTransitionRepository
    {
        /// <summary>
        /// Upserts a batch of transition records. Returns the number of rows committed.
        /// </summary>
        Task<int> StoreBatchAsync(IReadOnlyList<DecisionTransitionRecord> records, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads up to <paramref name="maxResults"/> transition records for a single session,
        /// ordered by <see cref="DecisionTransitionRecord.StepIndex"/> ascending. Used by the
        /// Inspector decision-graph endpoint (<c>GET /api/sessions/{id}/decision-graph</c>, Plan §M5).
        /// </summary>
        Task<List<DecisionTransitionRecord>> QueryBySessionAsync(
            string tenantId, string sessionId, int maxResults = 1000, CancellationToken cancellationToken = default);
    }
}
