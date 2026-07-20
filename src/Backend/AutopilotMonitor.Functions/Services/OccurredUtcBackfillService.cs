using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// One-time backfill of the <c>OccurredUtc</c> business-timestamp column on
    /// AuditLogs and OpsEvents rows written before the column existed. The value is
    /// decoded deterministically from the reverse-tick RowKey, so re-runs are
    /// idempotent and the write is a pure MERGE that adds exactly one property
    /// (nothing existing is modified or deleted). Audit legacy bare-GUID rows carry
    /// no recoverable time and are excluded server-side by the scan filter.
    /// Readers do not depend on this backfill (they decode the RowKey on the fly);
    /// its value is future-migration robustness and raw-query readability.
    /// </summary>
    public class OccurredUtcBackfillService
    {
        public const string TableAudit = "audit";
        public const string TableOps = "ops";

        private const int MaxSamplesInResponse = 100;
        private const int TransactionChunkSize = 100;

        private readonly TableStorageService _storage;
        private readonly ILogger<OccurredUtcBackfillService> _logger;

        public OccurredUtcBackfillService(TableStorageService storage, ILogger<OccurredUtcBackfillService> logger)
        {
            _storage = storage;
            _logger = logger;
        }

        public class BackfillResult
        {
            public string Table { get; set; } = string.Empty;
            public bool DryRun { get; set; }
            public int RowsExamined { get; set; }
            public int WouldWrite { get; set; }
            public int Written { get; set; }
            public int SkippedAlreadySet { get; set; }
            public int SkippedUndecodable { get; set; }
            public int Errors { get; set; }
            public string? NextContinuation { get; set; }
            public List<BackfillSample> Samples { get; set; } = new();
        }

        public class BackfillSample
        {
            public string PartitionKey { get; set; } = string.Empty;
            public string RowKey { get; set; } = string.Empty;
            public DateTime DecodedUtc { get; set; }
        }

        internal enum RowDecision { AlreadySet, Write, Undecodable }

        /// <summary>Pure per-row decision, extracted for unit testing.</summary>
        internal static RowDecision DecideRow(string table, TableEntity entity, out DateTime occurredUtc)
        {
            occurredUtc = default;
            if (BusinessTimestamp.GetUtcDateTime(entity, BusinessTimestamp.OccurredUtcColumn).HasValue)
                return RowDecision.AlreadySet;

            var decoded = table == TableAudit
                ? BusinessTimestamp.TryDecodeAuditRowKey(entity.RowKey, out occurredUtc)
                : BusinessTimestamp.TryDecodeOpsRowKey(entity.RowKey, out occurredUtc);
            return decoded ? RowDecision.Write : RowDecision.Undecodable;
        }

        public async Task<BackfillResult> RunAsync(string table, bool dryRun, int maxRows, string? continuation)
        {
            var (tableName, scanFilter) = table switch
            {
                // Audit: server-side restriction to time-encoded '!'-rows — legacy bare-GUID
                // rows (which sort after '"') have nothing to decode and are never touched.
                TableAudit => (Constants.TableNames.AuditLogs,
                    $"RowKey ge '{BusinessTimestamp.AuditRowKeyPrefix}' and RowKey lt '{BusinessTimestamp.AuditTimeEncodedUpperBound}'"),
                TableOps => (Constants.TableNames.OpsEvents, (string?)null),
                _ => throw new ArgumentException($"Unknown table '{table}'", nameof(table)),
            };

            var tableClient = _storage.GetTableClient(tableName);
            var result = new BackfillResult { Table = table, DryRun = dryRun };

            var (entities, nextToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: tableClient,
                filter: scanFilter,
                pageSize: maxRows,
                continuation: continuation,
                select: new[] { "PartitionKey", "RowKey", BusinessTimestamp.OccurredUtcColumn });
            result.NextContinuation = nextToken;

            var pending = new List<(string PartitionKey, string RowKey, DateTime OccurredUtc)>();
            foreach (var entity in entities)
            {
                result.RowsExamined++;
                switch (DecideRow(table, entity, out var occurredUtc))
                {
                    case RowDecision.AlreadySet:
                        result.SkippedAlreadySet++;
                        break;
                    case RowDecision.Undecodable:
                        result.SkippedUndecodable++;
                        _logger.LogWarning("OccurredUtc backfill: undecodable RowKey {PK}/{RK} in {Table}",
                            entity.PartitionKey, entity.RowKey, tableName);
                        break;
                    case RowDecision.Write:
                        result.WouldWrite++;
                        pending.Add((entity.PartitionKey, entity.RowKey, occurredUtc));
                        if (result.Samples.Count < MaxSamplesInResponse)
                        {
                            result.Samples.Add(new BackfillSample
                            {
                                PartitionKey = entity.PartitionKey,
                                RowKey = entity.RowKey,
                                DecodedUtc = occurredUtc,
                            });
                        }
                        break;
                }
            }

            if (!dryRun && pending.Count > 0)
                result.Written = await WritePendingAsync(tableClient, pending, result);

            _logger.LogInformation(
                "OccurredUtc backfill {Table}: dryRun={DryRun} examined={Examined} wouldWrite={WouldWrite} written={Written} alreadySet={AlreadySet} undecodable={Undecodable} errors={Errors} more={More}",
                table, dryRun, result.RowsExamined, result.WouldWrite, result.Written,
                result.SkippedAlreadySet, result.SkippedUndecodable, result.Errors, result.NextContinuation != null);
            return result;
        }

        private async Task<int> WritePendingAsync(TableClient tableClient,
            List<(string PartitionKey, string RowKey, DateTime OccurredUtc)> pending, BackfillResult result)
        {
            var written = 0;
            foreach (var partitionGroup in pending.GroupBy(p => p.PartitionKey))
            {
                foreach (var chunk in partitionGroup.Chunk(TransactionChunkSize))
                {
                    // Merge entity carries ONLY the new column; unconditional (ETag *) merge is
                    // safe because the value is a pure function of the immutable RowKey.
                    var actions = chunk.Select(p => new TableTransactionAction(
                        TableTransactionActionType.UpdateMerge,
                        new TableEntity(p.PartitionKey, p.RowKey)
                        {
                            [BusinessTimestamp.OccurredUtcColumn] = p.OccurredUtc,
                        },
                        ETag.All)).ToList();
                    try
                    {
                        await tableClient.SubmitTransactionAsync(actions);
                        written += actions.Count;
                    }
                    catch (Exception ex)
                    {
                        result.Errors += actions.Count;
                        _logger.LogError(ex, "OccurredUtc backfill: merge transaction failed for partition {PK} ({Count} rows)",
                            partitionGroup.Key, actions.Count);
                    }
                }
            }
            return written;
        }
    }
}
