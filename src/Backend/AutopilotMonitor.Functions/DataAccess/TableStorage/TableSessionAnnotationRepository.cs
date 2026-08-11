using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table-storage implementation of <see cref="ISessionAnnotationRepository"/>.
    /// Backed by the dedicated <c>SessionAnnotations</c> table:
    /// PK = tenantId, RK = <c>{sessionId}_{lane}</c>.
    /// </summary>
    public class TableSessionAnnotationRepository : ISessionAnnotationRepository
    {
        // Client-side ruleId filtering can shrink an Azure page below pageSize; the
        // back-fill loop keeps fetching until enough matches accumulate, capped here so
        // a pathological filter can never turn one request into an unbounded scan.
        internal const int MaxBackfillRoundTrips = 10;

        private readonly TableClient _tableClient;
        private readonly ILogger<TableSessionAnnotationRepository> _logger;

        public TableSessionAnnotationRepository(
            TableStorageService storage, ILogger<TableSessionAnnotationRepository> logger)
        {
            _tableClient = storage.GetTableClient(Constants.TableNames.SessionAnnotations);
            _logger = logger;
        }

        public async Task<SessionAnnotation?> GetAsync(string tenantId, string sessionId, string lane)
        {
            try
            {
                var entity = await _tableClient.GetEntityAsync<TableEntity>(
                    tenantId, BuildRowKey(sessionId, lane));
                return MapAnnotation(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error loading annotation {SessionId}/{Lane} for tenant {TenantId}",
                    sessionId, lane, tenantId);
                throw;
            }
        }

        public async Task<List<SessionAnnotation>> GetForSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                // '~' (0x7E) sorts after '_' (0x5F) and every lane letter: upper-bounds all
                // "{sessionId}_{lane}" keys without matching a longer sessionId prefix.
                var filter =
                    $"PartitionKey eq '{Escape(tenantId)}'" +
                    $" and RowKey ge '{Escape(sessionId)}_'" +
                    $" and RowKey lt '{Escape(sessionId)}_~'";
                var results = new List<SessionAnnotation>();
                await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapAnnotation(entity));
                }
                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error loading annotations for session {SessionId}, tenant {TenantId}",
                    sessionId, tenantId);
                throw;
            }
        }

        public async Task UpsertAsync(SessionAnnotation annotation)
        {
            try
            {
                await _tableClient.UpsertEntityAsync(StoreAnnotation(annotation), TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error saving annotation {SessionId}/{Lane} for tenant {TenantId}",
                    annotation.SessionId, annotation.Lane, annotation.TenantId);
                throw;
            }
        }

        public async Task DeleteAsync(string tenantId, string sessionId, string lane)
        {
            try
            {
                await _tableClient.DeleteEntityAsync(tenantId, BuildRowKey(sessionId, lane));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Missing row is a no-op — clearing an annotation twice must not fail.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error deleting annotation {SessionId}/{Lane} for tenant {TenantId}",
                    sessionId, lane, tenantId);
                throw;
            }
        }

        public async Task<(List<SessionAnnotation> Items, string? NextRawToken)> QueryPageAsync(
            string? tenantId,
            string? lane,
            string? verdict,
            string? ruleId,
            DateTime? dateFrom,
            DateTime? dateTo,
            int pageSize,
            string? continuation)
        {
            var filter = BuildQueryFilter(tenantId, lane, verdict, dateFrom, dateTo);
            var items = new List<SessionAnnotation>();
            var token = continuation;

            // ruleId cannot be pushed into OData (substring match on a JSON column), so it
            // filters client-side. Back-fill whole Azure pages until pageSize matches
            // accumulate or the scan is exhausted, so filtered-out rows never consume page
            // budget (lesson: filter-after-pagination empties the list).
            for (var round = 0; round < MaxBackfillRoundTrips; round++)
            {
                var (page, nextToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                    _tableClient, filter, pageSize, token);

                foreach (var entity in page)
                {
                    var annotation = MapAnnotation(entity);
                    if (ruleId != null &&
                        !annotation.RuleIds.Contains(ruleId, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    items.Add(annotation);
                }

                token = nextToken;
                if (token == null || items.Count >= pageSize)
                {
                    break;
                }
            }

            return (items, token);
        }

        internal static string BuildRowKey(string sessionId, string lane) => $"{sessionId}_{lane}";

        internal static string? BuildQueryFilter(
            string? tenantId, string? lane, string? verdict, DateTime? dateFrom, DateTime? dateTo)
        {
            var sb = new StringBuilder();
            void Append(string clause)
            {
                if (sb.Length > 0) sb.Append(" and ");
                sb.Append(clause);
            }

            if (!string.IsNullOrEmpty(tenantId)) Append($"PartitionKey eq '{Escape(tenantId!)}'");
            if (!string.IsNullOrEmpty(lane)) Append($"Lane eq '{Escape(lane!)}'");
            if (!string.IsNullOrEmpty(verdict)) Append($"Verdict eq '{Escape(verdict!)}'");
            if (dateFrom.HasValue) Append($"UpdatedAtUtc ge datetime'{dateFrom.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'");
            if (dateTo.HasValue) Append($"UpdatedAtUtc lt datetime'{dateTo.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}'");

            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string Escape(string value) => value.Replace("'", "''");

        // ── Store / Map (memory: feedback_table_storage_serialization) ─────────
        // internal static: pinned by round-trip unit tests — every property must
        // survive Store→Map, so any new field lands in BOTH methods plus the test.

        internal static TableEntity StoreAnnotation(SessionAnnotation a) =>
            new(a.TenantId, BuildRowKey(a.SessionId, a.Lane))
            {
                ["SessionId"] = a.SessionId,
                ["Lane"] = a.Lane,
                ["Verdict"] = a.Verdict,
                ["Note"] = a.Note,
                ["AuthorUpn"] = a.AuthorUpn,
                ["AuthorDisplayName"] = a.AuthorDisplayName,
                ["CreatedByUpn"] = a.CreatedByUpn,
                ["CreatedAtUtc"] = EnsureUtc(a.CreatedAtUtc),
                ["UpdatedAtUtc"] = EnsureUtc(a.UpdatedAtUtc),
                ["RuleIdsJson"] = JsonSerializer.Serialize(a.RuleIds),
            };

        internal static SessionAnnotation MapAnnotation(TableEntity e) => new()
        {
            TenantId = e.PartitionKey,
            SessionId = e.GetString("SessionId") ?? string.Empty,
            Lane = e.GetString("Lane") ?? string.Empty,
            Verdict = e.GetString("Verdict"),
            Note = e.GetString("Note"),
            AuthorUpn = e.GetString("AuthorUpn") ?? string.Empty,
            AuthorDisplayName = e.GetString("AuthorDisplayName") ?? string.Empty,
            CreatedByUpn = e.GetString("CreatedByUpn") ?? string.Empty,
            CreatedAtUtc = e.GetDateTime("CreatedAtUtc") ?? DateTime.MinValue,
            UpdatedAtUtc = e.GetDateTime("UpdatedAtUtc") ?? DateTime.MinValue,
            RuleIds = DeserializeRuleIds(e.GetString("RuleIdsJson")),
        };

        private static List<string> DeserializeRuleIds(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new List<string>();
            try
            {
                return JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>(); // corrupt/truncated column — degrade to empty, never throw on read
            }
        }

        private static DateTime EnsureUtc(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
