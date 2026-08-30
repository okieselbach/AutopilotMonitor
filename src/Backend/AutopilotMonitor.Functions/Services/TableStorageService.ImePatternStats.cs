using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// <c>ImePatternStats</c> — global per-IME-version pattern hit statistics
    /// (PartitionKey = IME version, RowKey = patternId). Store + Map live side by side here so a
    /// property written but not mapped cannot go unnoticed.
    /// </summary>
    public partial class TableStorageService
    {
        private const int ImePatternStatsBatchSize = 100;

        public async Task UpsertImePatternStatsAsync(string imeVersion, IReadOnlyDictionary<string, int> hits, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(imeVersion) || hits == null || hits.Count == 0) return;

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImePatternStats);

            // One partition read instead of one point read per pattern (~80 per session).
            var existing = new Dictionary<string, TableEntity>(StringComparer.OrdinalIgnoreCase);
            await foreach (var row in tableClient.QueryAsync<TableEntity>(e => e.PartitionKey == imeVersion))
                existing[row.RowKey] = row;

            var upserts = new List<TableEntity>(hits.Count);
            foreach (var kv in hits)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                var count = Math.Max(0, kv.Value);

                if (!existing.TryGetValue(kv.Key, out var entity))
                {
                    entity = new TableEntity(imeVersion, kv.Key)
                    {
                        ["Sessions"] = 0,
                        ["SessionsWithHit"] = 0,
                        ["Hits"] = 0L,
                    };
                }

                entity["Sessions"] = (entity.GetInt32("Sessions") ?? 0) + 1;
                if (count > 0)
                {
                    entity["SessionsWithHit"] = (entity.GetInt32("SessionsWithHit") ?? 0) + 1;
                    entity["Hits"] = (entity.GetInt64("Hits") ?? 0L) + count;
                    entity["LastHitAt"] = nowUtc;
                }
                entity["UpdatedAt"] = nowUtc;
                upserts.Add(entity);
            }

            for (var i = 0; i < upserts.Count; i += ImePatternStatsBatchSize)
            {
                var batch = upserts.Skip(i).Take(ImePatternStatsBatchSize)
                    .Select(e => new TableTransactionAction(TableTransactionActionType.UpsertReplace, e))
                    .ToList();
                if (batch.Count > 0)
                    await tableClient.SubmitTransactionAsync(batch);
            }
        }

        public async Task<List<ImePatternStatsEntry>> GetImePatternStatsAsync()
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImePatternStats);
            var result = new List<ImePatternStatsEntry>();
            try
            {
                await foreach (var row in tableClient.QueryAsync<TableEntity>(
                    select: new[] { "PartitionKey", "RowKey", "Sessions", "SessionsWithHit", "Hits", "LastHitAt", "UpdatedAt", "DriftFlaggedAt" }))
                {
                    result.Add(MapToImePatternStatsEntry(row));
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Table not created yet (first deploy before the sentinel pass) — empty is the truth.
            }
            return result;
        }

        public async Task<bool> TryMarkImePatternDriftFlaggedAsync(string imeVersion, string patternId, DateTime nowUtc)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImePatternStats);
            try
            {
                var response = await tableClient.GetEntityAsync<TableEntity>(imeVersion, patternId);
                var entity = response.Value;
                if (entity.GetDateTime("DriftFlaggedAt").HasValue) return false;
                entity["DriftFlaggedAt"] = nowUtc;
                // ETag-conditional: a concurrent flagger loses and reports false — one OpsEvent per cell.
                await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404 || ex.Status == 412)
            {
                return false;
            }
        }

        private static ImePatternStatsEntry MapToImePatternStatsEntry(TableEntity row) => new()
        {
            Version = row.PartitionKey,
            PatternId = row.RowKey,
            Sessions = row.GetInt32("Sessions") ?? 0,
            SessionsWithHit = row.GetInt32("SessionsWithHit") ?? 0,
            Hits = row.GetInt64("Hits") ?? 0L,
            LastHitAt = row.GetDateTime("LastHitAt"),
            UpdatedAt = row.GetDateTime("UpdatedAt") ?? DateTime.MinValue,
            DriftFlaggedAt = row.GetDateTime("DriftFlaggedAt"),
        };
    }
}
