using System;
using System.Collections.Generic;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Splits entities into entity-group transactions that respect all three Azure Tables
    /// limits at once: at most 100 actions per transaction, a transaction payload under 4 MiB
    /// and a single entity under 1 MiB. Every other writer used to chunk by count only, which
    /// let 100 payload-heavy rows (Signals with chunked PayloadJson, Events with 30k-char
    /// Message + DataJson) exceed the 4 MiB cap and surface as an opaque 500 the agent
    /// retries forever (audit 2026-09-05 F07).
    /// <para>
    /// Sizes are estimates on the safe side (UTF-16 width for strings, base64 growth for
    /// binaries, a fixed envelope per entity); the budget leaves headroom for the OData
    /// batch framing. A batch never spans partitions: a PartitionKey change starts a new
    /// transaction, so callers may pass a pre-grouped sequence and rely on the order.
    /// </para>
    /// </summary>
    internal static class TableTransactionBatcher
    {
        /// <summary>Azure Tables hard limit on actions per entity-group transaction.</summary>
        public const int MaxActionsPerTransaction = 100;

        /// <summary>Conservative payload budget per transaction (service limit: 4 MiB).</summary>
        public const int TransactionByteBudget = 3_500_000;

        /// <summary>Conservative size guard per entity (service limit: 1 MiB).</summary>
        public const int EntityByteLimit = 1_000_000;

        private const int EntityEnvelopeBytes = 256;
        private const int ScalarPropertyBytes = 48;

        /// <summary>
        /// Groups <paramref name="entities"/> into transactions of <paramref name="actionType"/>
        /// actions. Preserves order; starts a new transaction on the action cap, the byte
        /// budget or a PartitionKey change. Throws <see cref="TableEntityTooLargeException"/>
        /// for an entity that can never fit on its own — splitting cannot fix that, the caller
        /// has to shrink or reject the row.
        /// </summary>
        public static List<List<TableTransactionAction>> Split(
            IEnumerable<TableEntity> entities,
            TableTransactionActionType actionType,
            int maxActions = MaxActionsPerTransaction,
            int byteBudget = TransactionByteBudget)
        {
            if (entities == null) throw new ArgumentNullException(nameof(entities));
            if (maxActions < 1 || maxActions > MaxActionsPerTransaction)
                throw new ArgumentOutOfRangeException(nameof(maxActions));
            if (byteBudget < 1) throw new ArgumentOutOfRangeException(nameof(byteBudget));

            var batches = new List<List<TableTransactionAction>>();
            var current = new List<TableTransactionAction>();
            var currentBytes = 0;
            string? currentPartition = null;

            foreach (var entity in entities)
            {
                var bytes = EstimateEntityBytes(entity);
                if (bytes > EntityByteLimit)
                    throw new TableEntityTooLargeException(entity.PartitionKey, entity.RowKey, bytes);

                var partitionChanged = currentPartition != null
                    && !string.Equals(currentPartition, entity.PartitionKey, StringComparison.Ordinal);
                if (current.Count > 0
                    && (partitionChanged || current.Count >= maxActions || currentBytes + bytes > byteBudget))
                {
                    batches.Add(current);
                    current = new List<TableTransactionAction>();
                    currentBytes = 0;
                }

                current.Add(new TableTransactionAction(actionType, entity));
                currentBytes += bytes;
                currentPartition = entity.PartitionKey;
            }

            if (current.Count > 0) batches.Add(current);
            return batches;
        }

        /// <summary>
        /// Upper-bound estimate of the serialized size of one entity: UTF-16 width for strings
        /// (property names included), base64 growth for binaries, a fixed cost for every
        /// scalar, plus the per-entity envelope.
        /// </summary>
        public static int EstimateEntityBytes(TableEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            long total = EntityEnvelopeBytes
                + 2L * (entity.PartitionKey?.Length ?? 0)
                + 2L * (entity.RowKey?.Length ?? 0);

            foreach (var kv in entity)
            {
                // TableEntity enumerates the keys as properties too; they are counted above.
                if (kv.Key == "PartitionKey" || kv.Key == "RowKey") continue;

                total += 2L * kv.Key.Length;
                total += kv.Value switch
                {
                    null => 0,
                    string s => 2L * s.Length,
                    byte[] b => (b.Length * 4L + 2) / 3,
                    BinaryData bd => (bd.ToMemory().Length * 4L + 2) / 3,
                    _ => ScalarPropertyBytes,
                };
            }

            return total > int.MaxValue ? int.MaxValue : (int)total;
        }
    }

    /// <summary>
    /// A single entity exceeds what Azure Tables accepts in one row. Permanent by nature: no
    /// retry and no smaller batch can make it fit. The ingest maps it to HTTP 413 so the agent
    /// isolates the item instead of replaying the batch.
    /// </summary>
    public sealed class TableEntityTooLargeException : Exception
    {
        public string PartitionKey { get; }
        public string RowKey { get; }
        public int EstimatedBytes { get; }

        public TableEntityTooLargeException(string partitionKey, string rowKey, int estimatedBytes)
            : base($"Entity {partitionKey}/{rowKey} is about {estimatedBytes} bytes, above the {TableTransactionBatcher.EntityByteLimit}-byte entity limit")
        {
            PartitionKey = partitionKey;
            RowKey = rowKey;
            EstimatedBytes = estimatedBytes;
        }
    }
}
