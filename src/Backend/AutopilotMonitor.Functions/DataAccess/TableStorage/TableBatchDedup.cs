using System;
using System.Collections.Generic;
using System.Linq;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Collapses entities sharing a (PartitionKey, RowKey) within a single batch down to the
    /// last occurrence (idempotent <see cref="TableTransactionActionType.UpsertReplace"/> →
    /// last-write-wins). Azure Table Storage rejects the <em>entire</em> entity-group-transaction
    /// with HTTP 400 / <c>InvalidDuplicateRow</c> if any two actions target the same RowKey, so a
    /// duplicate-keyed record (e.g. an agent replaying an overlapping signal ordinal) would
    /// otherwise fail the whole ingest batch. Callers must group by PartitionKey before submitting;
    /// the (PK, RK) key here keeps the helper correct even if that precondition is ever relaxed.
    /// </summary>
    internal static class TableBatchDedup
    {
        /// <summary>
        /// Returns the de-duplicated entities (unique by (PartitionKey, RowKey), last-wins) plus the
        /// number of rows dropped. Output is PK/RowKey-ordered for deterministic chunk boundaries.
        /// </summary>
        internal static (List<TableEntity> Deduped, int Dropped) ByRowKey(IEnumerable<TableEntity> entities)
        {
            var byKey = new Dictionary<(string Pk, string Rk), TableEntity>();
            var dropped = 0;

            foreach (var e in entities)
            {
                var key = (e.PartitionKey, e.RowKey);
                if (byKey.ContainsKey(key)) dropped++;
                byKey[key] = e; // last-wins
            }

            var deduped = byKey.Values
                .OrderBy(e => e.PartitionKey, StringComparer.Ordinal)
                .ThenBy(e => e.RowKey, StringComparer.Ordinal)
                .ToList();

            return (deduped, dropped);
        }
    }
}
