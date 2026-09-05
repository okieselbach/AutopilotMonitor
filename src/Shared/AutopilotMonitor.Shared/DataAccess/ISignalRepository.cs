using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for the <c>Signals</c> primary table (Plan §M5). Writes projected
    /// <see cref="SignalRecord"/>s derived from agent-emitted DecisionSignals.
    /// <para>
    /// Writes are idempotent via <c>(PartitionKey, RowKey)</c>: a retried batch replays
    /// without duplicating rows. Records within a single batch must share the same
    /// (TenantId, SessionId) — the repository groups/chunks per the Azure Tables 100-op
    /// transaction limit.
    /// </para>
    /// </summary>
    /// <summary>Shared limits for session-scoped signal reads (see <see cref="ISignalRepository.QueryBySessionAsync"/>).</summary>
    public static class SignalQueryLimits
    {
        /// <summary>
        /// Cumulative <see cref="SignalRecord.PayloadJson"/> characters a single session read may
        /// hold in memory. 32 M chars (~64 MB UTF-16) is far above any real session — a full
        /// 5000-signal stream of typical payloads is well under 5 M chars — but small enough
        /// that one poisoned session cannot push a worker past its memory limit.
        /// </summary>
        public const long DefaultMaxTotalPayloadChars = 32L * 1024 * 1024;

        /// <summary>
        /// True when <paramref name="signals"/> filled the payload budget, i.e. the read may have
        /// stopped before the session's last row. Mirrors the repository's stop condition.
        /// </summary>
        public static bool IsPayloadBudgetExhausted(IReadOnlyList<SignalRecord> signals, long maxTotalPayloadChars)
        {
            long total = 0;
            for (var i = 0; i < signals.Count; i++)
            {
                total += signals[i].PayloadJson?.Length ?? 0;
                if (total >= maxTotalPayloadChars) return true;
            }
            return false;
        }
    }

    public interface ISignalRepository
    {
        /// <summary>
        /// Upserts a batch of signal records. Returns the number of rows committed.
        /// </summary>
        Task<int> StoreBatchAsync(IReadOnlyList<SignalRecord> records, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads up to <paramref name="maxResults"/> signal records for a single session,
        /// ordered by <see cref="SignalRecord.SessionSignalOrdinal"/> ascending. Used by the
        /// Inspector read endpoint (<c>GET /api/sessions/{id}/signals</c>, Plan §M5) and the
        /// reducer-verification replay.
        /// <para>
        /// <paramref name="maxTotalPayloadChars"/> bounds the cumulative
        /// <see cref="SignalRecord.PayloadJson"/> length held in memory: the payloads are
        /// device-uploaded and chunk-stored up to the ~1 MB entity limit, so a row cap alone
        /// leaves peak memory at rows × 1 MB. Rows are appended until the budget is reached
        /// (the row that crosses it is still included); callers detect truncation with
        /// <see cref="SignalQueryLimits.IsPayloadBudgetExhausted"/>.
        /// </para>
        /// </summary>
        Task<List<SignalRecord>> QueryBySessionAsync(
            string tenantId,
            string sessionId,
            int maxResults = 1000,
            CancellationToken cancellationToken = default,
            long maxTotalPayloadChars = SignalQueryLimits.DefaultMaxTotalPayloadChars);
    }
}
