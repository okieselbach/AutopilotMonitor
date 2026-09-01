using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IOpsEventRepository"/>.
    /// PartitionKey = Category (Consent, Maintenance, Security, Tenant, Agent).
    /// RowKey = reverse-tick for newest-first ordering.
    /// </summary>
    public class TableOpsEventRepository : IOpsEventRepository
    {
        private readonly TableClient _table;
        private readonly ILogger<TableOpsEventRepository> _logger;

        public TableOpsEventRepository(
            TableStorageService storage,
            ILogger<TableOpsEventRepository> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.OpsEvents);
        }

        public async Task SaveOpsEventAsync(OpsEventEntry entry)
        {
            var pk = entry.Category;
            var rk = RowKeyCodec.InvertedTicks(DateTime.UtcNow);

            var entity = new TableEntity(pk, rk)
            {
                ["EventType"] = Truncate(entry.EventType, 64),
                ["Severity"]  = Truncate(entry.Severity, 16),
                ["TenantId"]  = Truncate(entry.TenantId, 36),
                ["UserId"]    = Truncate(entry.UserId, 128),
                ["Message"]   = Truncate(entry.Message, 512),
                ["Details"]   = Truncate(entry.Details, 4096),
                // "Timestamp" is a reserved system property (supplied values are ignored and
                // any row rewrite resets it), so the event time needs its own column.
                [BusinessTimestamp.OccurredUtcColumn] = entry.Timestamp,
            };

            await _table.UpsertEntityAsync(entity);
        }

        public async Task<List<OpsEventEntry>> GetOpsEventsAsync(
            string? category = null, DateTime? dateFrom = null, DateTime? dateTo = null,
            OpsEventQueryFilters? filters = null)
        {
            var result = new List<OpsEventEntry>();
            var filter = BuildFilter(category, dateFrom, dateTo, filters);
            var query = string.IsNullOrEmpty(filter)
                ? _table.QueryAsync<TableEntity>()
                : _table.QueryAsync<TableEntity>(filter: filter);

            await foreach (var entity in query)
            {
                result.Add(MapToEntry(entity));
            }
            // Cross-partition order is undefined; sort newest-first to match prior behaviour.
            result.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return result;
        }

        public async Task<RawPage<OpsEventEntry>> GetOpsEventsPageAsync(
            string? category, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            OpsEventQueryFilters? filters = null)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            try
            {
                // Single-category path: PK-targeted query, RowKey is reverse-tick →
                // Azure-native (PK asc, RK asc) already yields newest-first. No re-sort.
                if (!string.IsNullOrEmpty(category))
                {
                    var filter = BuildFilter(category, dateFrom, dateTo, filters);
                    var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                        client: _table,
                        filter: filter,
                        pageSize: pageSize,
                        continuation: continuation);

                    var page = new List<OpsEventEntry>(entities.Count);
                    foreach (var entity in entities) page.Add(MapToEntry(entity));
                    return new RawPage<OpsEventEntry>(page, nextRawToken);
                }

                // All-category path: per-partition fan-out + merge-sort. Azure pages
                // cross-partition queries by (PK asc, RK asc), so without this fan-out
                // the first page would come entirely from the alphabetically-first
                // category — defeating "newest first globally". Mirrors the same
                // pattern used for cross-tenant session queries.
                return await FanOutAcrossCategoriesAsync(dateFrom, dateTo, pageSize, continuation, filters);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get ops events page");
                return RawPage<OpsEventEntry>.Empty;
            }
        }

        // Fixed list, owned by OpsEventCategory.All so it cannot drift from the vocabulary. It did
        // once: Platform (Azure Monitor alerts) was written but never listed, so every PAGED
        // cross-category read silently skipped it while the unpaged full-table path still showed
        // it. New categories are rare and need a deploy anyway, so a runtime probe of the table
        // partitions would just trade certainty for cost — the coverage test guards the list instead.
        internal static readonly string[] AllCategories = OpsEventCategory.All;

        private async Task<RawPage<OpsEventEntry>> FanOutAcrossCategoriesAsync(
            DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation,
            OpsEventQueryFilters? filters = null)
        {
            var continuations = PerPartitionFanOutMerge.DecodeMultiContinuation(continuation);
            // First-page request fans out across every category; subsequent
            // pages restrict to whatever is still active in the continuation
            // map. With only 6 fixed categories this never grew to a problem
            // size, but the convention now matches the audit fan-out so the
            // shared fan-out helper has one rule.
            var isFirstPage = string.IsNullOrEmpty(continuation);

            var activeCats = AllCategories
                .Where(cat => isFirstPage || continuations.ContainsKey(cat))
                .ToList();
            if (activeCats.Count == 0)
                return new RawPage<OpsEventEntry>(new List<OpsEventEntry>(), null);

            var fetchTasks = activeCats.Select(async cat =>
            {
                continuations.TryGetValue(cat, out var catContinuation);
                var filter = BuildFilterWithRowKeyBound(cat, dateFrom, dateTo, catContinuation?.LastRowKey, filters);

                var fetched = new List<(string RowKey, OpsEventEntry Item)>();
                await foreach (var e in _table.QueryAsync<TableEntity>(filter: filter, maxPerPage: pageSize))
                {
                    fetched.Add((e.RowKey, MapToEntry(e)));
                    if (fetched.Count >= pageSize) break;
                }
                return new PerPartitionFanOutMerge.PartitionFetchResult<OpsEventEntry>(cat, fetched);
            }).ToList();

            var results = await Task.WhenAll(fetchTasks);

            var (items, nextContinuations) = PerPartitionFanOutMerge.MergeAndAdvance(
                results, continuations, pageSize, e => e.Timestamp);

            // Map only carries active partitions; empty = pagination complete.
            string? nextRawToken = nextContinuations.Count > 0
                ? PerPartitionFanOutMerge.EncodeMultiContinuation(nextContinuations)
                : null;
            return new RawPage<OpsEventEntry>(items, nextRawToken);
        }

        internal static string BuildFilterWithRowKeyBound(
            string category, DateTime? dateFrom, DateTime? dateTo, string? lastRowKey,
            OpsEventQueryFilters? filters = null)
        {
            var clauses = new List<string>
            {
                $"PartitionKey eq '{category.Replace("'", "''")}'",
            };
            if (!string.IsNullOrEmpty(lastRowKey))
                clauses.Add($"RowKey gt '{lastRowKey!.Replace("'", "''")}'");
            if (dateFrom.HasValue)
                clauses.Add(BusinessTimestamp.OpsDateFromClause(ToUtc(dateFrom.Value)));
            if (dateTo.HasValue)
                clauses.Add(BusinessTimestamp.OpsDateToClause(ToUtc(dateTo.Value)));
            AppendFieldFilters(clauses, filters);
            return string.Join(" and ", clauses);
        }

        /// <summary>
        /// Appends the optional non-key field filters (eventType / severity / minSeverity) as
        /// SERVER-SIDE clauses. Both filter builders funnel through here so the paged fan-out and
        /// the single-category / unpaged paths can never honour a different filter surface — a
        /// drift would make the same query return different rows depending on whether a category
        /// was named. minSeverity expands to an OR-set instead of a range comparison because Table
        /// Storage compares strings lexicographically ("Critical" &lt; "Error" &lt; "Info" &lt;
        /// "Warning"), which has nothing to do with severity order.
        /// </summary>
        private static void AppendFieldFilters(List<string> clauses, OpsEventQueryFilters? filters)
        {
            if (filters == null || filters.IsEmpty) return;
            if (!string.IsNullOrEmpty(filters.EventType))
                clauses.Add($"EventType eq '{filters.EventType!.Replace("'", "''")}'");
            if (!string.IsNullOrEmpty(filters.Severity))
                clauses.Add($"Severity eq '{filters.Severity!.Replace("'", "''")}'");
            if (!string.IsNullOrEmpty(filters.MinSeverity))
            {
                var allowed = OpsEventSeverity.AtOrAbove(filters.MinSeverity!);
                // Info is the floor — every canonical severity qualifies, so the clause would be a
                // no-op that only costs query length. An unknown value yields an empty set; the
                // endpoint rejects those up front (400), so this stays defensive-only.
                if (allowed.Count > 0 && allowed.Count < OpsEventSeverity.All.Length)
                {
                    var terms = allowed.Select(s => $"Severity eq '{s}'");
                    clauses.Add($"({string.Join(" or ", terms)})");
                }
            }
        }

        // Date windows filter on the RowKey (reverse-tick, index-backed): the system
        // Timestamp is reset by storage migrations, and an OccurredUtc property filter
        // would exclude rows written before that column existed.
        internal static string? BuildFilter(
            string? category, DateTime? dateFrom, DateTime? dateTo, OpsEventQueryFilters? filters = null)
        {
            var clauses = new List<string>();
            if (!string.IsNullOrEmpty(category))
            {
                clauses.Add($"PartitionKey eq '{category!.Replace("'", "''")}'");
            }
            if (dateFrom.HasValue)
            {
                clauses.Add(BusinessTimestamp.OpsDateFromClause(ToUtc(dateFrom.Value)));
            }
            if (dateTo.HasValue)
            {
                clauses.Add(BusinessTimestamp.OpsDateToClause(ToUtc(dateTo.Value)));
            }
            AppendFieldFilters(clauses, filters);
            return clauses.Count == 0 ? null : string.Join(" and ", clauses);
        }

        private static DateTime ToUtc(DateTime dt)
            => dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();

        public async Task<int> DeleteOpsEventsOlderThanAsync(DateTime cutoff)
        {
            var deleted = 0;

            // Server-side RowKey range instead of a full-table scan over the system
            // Timestamp: every ops RowKey is a reverse-tick, so age is index-derivable,
            // and the system Timestamp lies after storage migrations (it would freeze
            // deletions for one retention period, then drop the whole pre-migration
            // corpus in a single run).
            var filter = BusinessTimestamp.OpsRetentionClause(cutoff);
            await foreach (var entity in _table.QueryAsync<TableEntity>(
                filter: filter, select: new[] { "PartitionKey", "RowKey" }))
            {
                try
                {
                    await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete ops event {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                }
            }

            return deleted;
        }

        internal static OpsEventEntry MapToEntry(TableEntity entity)
        {
            return new OpsEventEntry
            {
                Id        = $"{entity.PartitionKey}_{entity.RowKey}",
                Category  = entity.PartitionKey,
                EventType = entity.GetString("EventType") ?? string.Empty,
                Severity  = entity.GetString("Severity") ?? OpsEventSeverity.Info,
                TenantId  = entity.GetString("TenantId"),
                UserId    = entity.GetString("UserId"),
                Message   = entity.GetString("Message") ?? string.Empty,
                Details   = entity.GetString("Details"),
                Timestamp = ResolveTimestamp(entity),
            };
        }

        // Business time: OccurredUtc column → reverse-tick RowKey decode → system
        // Timestamp (last resort; a row rewrite — e.g. storage migration — resets it).
        internal static DateTime ResolveTimestamp(TableEntity entity)
        {
            var occurred = BusinessTimestamp.GetUtcDateTime(entity, BusinessTimestamp.OccurredUtcColumn);
            if (occurred.HasValue)
                return occurred.Value;
            if (BusinessTimestamp.TryDecodeOpsRowKey(entity.RowKey, out var decoded))
                return decoded;
            return BusinessTimestamp.GetUtcDateTime(entity, "Timestamp") ?? DateTime.MinValue;
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (value == null) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
