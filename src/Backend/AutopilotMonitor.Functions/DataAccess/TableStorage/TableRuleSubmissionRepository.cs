using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table-storage implementation of <see cref="IRuleSubmissionRepository"/>.
    /// PK = <see cref="PartitionKey"/> for every tenant, RK = inverted ticks + id (newest first).
    /// </summary>
    public class TableRuleSubmissionRepository : IRuleSubmissionRepository
    {
        internal const string PartitionKey = "submissions";

        private readonly TableClient _tableClient;
        private readonly ILogger<TableRuleSubmissionRepository> _logger;

        public TableRuleSubmissionRepository(TableStorageService storage, ILogger<TableRuleSubmissionRepository> logger)
        {
            _tableClient = storage.GetTableClient(Constants.TableNames.RuleSubmissions);
            _logger = logger;
        }

        public async Task<bool> AddAsync(RuleSubmission submission)
        {
            try
            {
                await _tableClient.AddEntityAsync(StoreSubmission(submission));
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store rule submission {SubmissionId}", submission.SubmissionId);
                return false;
            }
        }

        public async Task<RuleSubmission?> GetAsync(string submissionId)
        {
            var entity = await FindEntityAsync(submissionId);
            return entity == null ? null : MapSubmission(entity);
        }

        public async Task<List<RuleSubmission>> GetForTenantAsync(string tenantId)
        {
            var results = new List<RuleSubmission>();
            try
            {
                await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter: BuildFilter(tenantId, status: null)))
                {
                    results.Add(MapSubmission(entity));
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("RuleSubmissions table does not exist yet, returning empty list");
            }
            return results;
        }

        public async Task<RawPage<RuleSubmission>> GetPageAsync(string? tenantId, string? status, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            try
            {
                var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                    client: _tableClient,
                    filter: BuildFilter(tenantId, status),
                    pageSize: pageSize,
                    continuation: continuation);

                var page = new List<RuleSubmission>(entities.Count);
                foreach (var entity in entities) page.Add(MapSubmission(entity));
                // RowKey is the inverted tick of SubmittedAt — newest first by construction.
                return new RawPage<RuleSubmission>(page, nextRawToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("RuleSubmissions table does not exist yet, returning empty page");
                return RawPage<RuleSubmission>.Empty;
            }
        }

        public async Task<bool> UpdateAsync(RuleSubmission submission)
        {
            var existing = await FindEntityAsync(submission.SubmissionId);
            if (existing == null)
            {
                _logger.LogWarning("UpdateAsync: rule submission {SubmissionId} not found", submission.SubmissionId);
                return false;
            }

            var entity = StoreSubmission(submission);
            entity.RowKey = existing.RowKey;
            entity.ETag = existing.ETag;
            try
            {
                // If-Match on the ETag: two reviewers deciding the same submission cannot both win.
                await _tableClient.UpdateEntityAsync(entity, existing.ETag, TableUpdateMode.Replace);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                _logger.LogWarning("UpdateAsync: rule submission {SubmissionId} changed underneath the reviewer", submission.SubmissionId);
                return false;
            }
        }

        public async Task<HashSet<string>> GetReservedPublishedRuleIdsAsync()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
                    filter: BuildFilter(tenantId: null, status: RuleSubmissionStatuses.Approved),
                    select: new[] { "PublishedRuleId" }))
                {
                    var id = entity.GetString("PublishedRuleId");
                    if (!string.IsNullOrEmpty(id)) ids.Add(id!);
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("RuleSubmissions table does not exist yet");
            }
            return ids;
        }

        private async Task<TableEntity?> FindEntityAsync(string submissionId)
        {
            try
            {
                await foreach (var entity in _tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{PartitionKey}' and SubmissionId eq '{Escape(submissionId)}'"))
                {
                    return entity;
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("RuleSubmissions table does not exist yet");
            }
            return null;
        }

        internal static string BuildFilter(string? tenantId, string? status)
        {
            var filter = $"PartitionKey eq '{PartitionKey}'";
            if (!string.IsNullOrEmpty(tenantId)) filter += $" and TenantId eq '{Escape(tenantId!)}'";
            if (!string.IsNullOrEmpty(status)) filter += $" and Status eq '{Escape(status!)}'";
            return filter;
        }

        private static string Escape(string value) => value.Replace("'", "''");

        // ── Store / Map ─────────────────────────────────────────────────────────
        // internal static: pinned by round-trip unit tests — every property must survive
        // Store→Map, so any new field lands in BOTH methods plus the test.

        internal static TableEntity StoreSubmission(RuleSubmission s) =>
            new(PartitionKey, $"{RowKeyCodec.InvertedTicks(s.SubmittedAt)}_{s.SubmissionId}")
            {
                ["SubmissionId"] = s.SubmissionId,
                ["BatchId"] = s.BatchId,
                ["TenantId"] = s.TenantId,
                ["RuleKind"] = s.RuleKind,
                ["SourceRuleId"] = s.SourceRuleId,
                ["Title"] = s.Title,
                ["Category"] = s.Category,
                ["RuleJson"] = s.RuleJson,
                ["Comment"] = s.Comment ?? string.Empty,
                ["SubmittedBy"] = s.SubmittedBy,
                ["SubmittedByName"] = s.SubmittedByName,
                ["AttributionMode"] = s.AttributionMode,
                ["AttributionName"] = s.AttributionName,
                ["SubmittedAt"] = s.SubmittedAt,
                ["Status"] = s.Status,
                ["ValidationFindingsJson"] = JsonSerializer.Serialize(s.ValidationFindings),
                ["SourceFireStatsJson"] = s.SourceFireStats == null ? string.Empty : JsonSerializer.Serialize(s.SourceFireStats),
                ["ReviewedBy"] = s.ReviewedBy ?? string.Empty,
                ["ReviewedAt"] = s.ReviewedAt,
                ["ReviewComment"] = s.ReviewComment ?? string.Empty,
                ["WillBeAdapted"] = s.WillBeAdapted,
                ["PublishedRuleId"] = s.PublishedRuleId ?? string.Empty,
                ["DerivedFromTemplateRuleId"] = s.DerivedFromTemplateRuleId ?? string.Empty,
            };

        internal static RuleSubmission MapSubmission(TableEntity e) => new()
        {
            SubmissionId = e.GetString("SubmissionId") ?? string.Empty,
            BatchId = e.GetString("BatchId") ?? string.Empty,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            RuleKind = e.GetString("RuleKind") ?? string.Empty,
            SourceRuleId = e.GetString("SourceRuleId") ?? string.Empty,
            Title = e.GetString("Title") ?? string.Empty,
            Category = e.GetString("Category") ?? string.Empty,
            RuleJson = e.GetString("RuleJson") ?? string.Empty,
            Comment = NullIfEmpty(e.GetString("Comment")),
            SubmittedBy = e.GetString("SubmittedBy") ?? string.Empty,
            SubmittedByName = e.GetString("SubmittedByName") ?? string.Empty,
            AttributionMode = e.GetString("AttributionMode") ?? RuleAttributionModes.Anonymous,
            AttributionName = e.GetString("AttributionName") ?? RuleAttributionModes.AnonymousAuthor,
            SubmittedAt = e.GetDateTimeOffset("SubmittedAt")?.UtcDateTime ?? DateTime.MinValue,
            Status = e.GetString("Status") ?? RuleSubmissionStatuses.Pending,
            ValidationFindings = DeserializeOrDefault<List<RuleSubmissionFinding>>(e.GetString("ValidationFindingsJson")) ?? new List<RuleSubmissionFinding>(),
            SourceFireStats = DeserializeOrDefault<RuleSubmissionFireStats>(e.GetString("SourceFireStatsJson")),
            ReviewedBy = NullIfEmpty(e.GetString("ReviewedBy")),
            ReviewedAt = e.GetDateTimeOffset("ReviewedAt")?.UtcDateTime,
            ReviewComment = NullIfEmpty(e.GetString("ReviewComment")),
            WillBeAdapted = e.GetBoolean("WillBeAdapted") ?? false,
            PublishedRuleId = NullIfEmpty(e.GetString("PublishedRuleId")),
            DerivedFromTemplateRuleId = NullIfEmpty(e.GetString("DerivedFromTemplateRuleId")),
        };

        private static T? DeserializeOrDefault<T>(string? json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(json!);
            }
            catch (JsonException)
            {
                // A corrupt column must not hide the submission itself.
                return null;
            }
        }

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
