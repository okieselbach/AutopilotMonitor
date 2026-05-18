using System.Collections.Generic;
using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// GlobalAdmin-only read + delete endpoints over <see cref="ITenantCustomsArchiveRepository"/>.
/// Backs the <c>/global/customs-archive</c> Web UI: list runs, inspect entries, delete per
/// entry / per run. All writes audit-logged under <see cref="Constants.AuditGlobalTenantId"/>.
/// <para>
/// Authorization is enforced by <c>PolicyEnforcementMiddleware</c> via
/// <c>EndpointAccessPolicyCatalog</c>; the five routes registered here all carry
/// <c>EndpointPolicy.GlobalAdminOnly</c>.
/// </para>
/// </summary>
public class CustomsArchiveQueryFunction
{
    private readonly ILogger<CustomsArchiveQueryFunction> _logger;
    private readonly ITenantCustomsArchiveRepository _archive;
    private readonly IMaintenanceRepository _maintenance;

    public CustomsArchiveQueryFunction(
        ILogger<CustomsArchiveQueryFunction> logger,
        ITenantCustomsArchiveRepository archive,
        IMaintenanceRepository maintenance)
    {
        _logger = logger;
        _archive = archive;
        _maintenance = maintenance;
    }

    /// <summary>
    /// GET /api/global/customs-archive — list every archive partition (one entry per
    /// (tenantId, historyRowKey) run) with aggregated row-counts per source table.
    /// Optional ?tenantId= filter restricts to one tenant.
    /// </summary>
    [Function("CustomsArchiveListRuns")]
    public async Task<HttpResponseData> ListRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/customs-archive")] HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
        var filterTenantId = query["tenantId"];

        var summaries = new Dictionary<string, RunSummary>(System.StringComparer.Ordinal);

        var source = string.IsNullOrEmpty(filterTenantId)
            ? _archive.QueryAllAsync()
            : _archive.QueryByTenantAsync(filterTenantId.ToLowerInvariant());

        await foreach (var entry in source)
        {
            var key = entry.PartitionKey;
            if (!summaries.TryGetValue(key, out var summary))
            {
                summary = new RunSummary
                {
                    PartitionKey = entry.PartitionKey,
                    TenantId = entry.TenantId,
                    HistoryRowKey = entry.HistoryRowKey,
                    ArchivedAt = entry.ArchivedAt,
                };
                summaries[key] = summary;
            }

            switch (entry.OriginalTable)
            {
                case Constants.TableNames.GatherRules: summary.GatherRulesCount++; break;
                case Constants.TableNames.AnalyzeRules: summary.AnalyzeRulesCount++; break;
                case Constants.TableNames.ImeLogPatterns: summary.ImeLogPatternsCount++; break;
            }

            // Track the earliest ArchivedAt for the run (in case rows have slightly drifting
            // timestamps from multiple iterations of archive-then-wipe).
            if (entry.ArchivedAt < summary.ArchivedAt)
            {
                summary.ArchivedAt = entry.ArchivedAt;
            }
        }

        var runs = summaries.Values
            .OrderByDescending(s => s.ArchivedAt)
            .ToList();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, count = runs.Count, runs });
        return response;
    }

    /// <summary>
    /// GET /api/global/customs-archive/{tenantId}/{historyRowKey} — list every archived
    /// entry in one offboarding run, with a short preview of <see cref="TenantOffboardingCustomsArchiveEntry.EntityJson"/>.
    /// Full body is available via the per-entry route below.
    /// </summary>
    [Function("CustomsArchiveListEntries")]
    public async Task<HttpResponseData> ListEntries(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "global/customs-archive/{tenantId}/{historyRowKey}")] HttpRequestData req,
        string tenantId,
        string historyRowKey)
    {
        var normalizedTenantId = tenantId.ToLowerInvariant();
        var items = new List<EntrySummary>();
        await foreach (var entry in _archive.QueryByRunAsync(normalizedTenantId, historyRowKey))
        {
            items.Add(new EntrySummary
            {
                PartitionKey = entry.PartitionKey,
                RowKey = entry.RowKey,
                OriginalTable = entry.OriginalTable,
                OriginalRowKey = entry.OriginalRowKey,
                ArchivedAt = entry.ArchivedAt,
                EntityJsonPreview = TruncatePreview(entry.EntityJson, maxLength: 200),
            });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, count = items.Count, entries = items });
        return response;
    }

    /// <summary>
    /// GET /api/global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey} — full
    /// archive entry including <see cref="TenantOffboardingCustomsArchiveEntry.EntityJson"/>.
    /// </summary>
    [Function("CustomsArchiveGetEntry")]
    public async Task<HttpResponseData> GetEntry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
            Route = "global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey}")] HttpRequestData req,
        string tenantId,
        string historyRowKey,
        string archiveRowKey)
    {
        var partitionKey = TableTenantCustomsArchiveRepository.BuildPartitionKey(
            tenantId.ToLowerInvariant(), historyRowKey);
        var entry = await _archive.TryGetEntryAsync(partitionKey, archiveRowKey);
        if (entry == null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, entry });
        return response;
    }

    /// <summary>
    /// DELETE /api/global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey} —
    /// remove a single archived entry. Audit-logged under <see cref="Constants.AuditGlobalTenantId"/>.
    /// </summary>
    [Function("CustomsArchiveDeleteEntry")]
    public async Task<HttpResponseData> DeleteEntry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete",
            Route = "global/customs-archive/{tenantId}/{historyRowKey}/{archiveRowKey}")] HttpRequestData req,
        string tenantId,
        string historyRowKey,
        string archiveRowKey,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var normalizedTenantId = tenantId.ToLowerInvariant();
        var partitionKey = TableTenantCustomsArchiveRepository.BuildPartitionKey(normalizedTenantId, historyRowKey);

        await _archive.DeleteEntryAsync(partitionKey, archiveRowKey);

        await _maintenance.LogAuditEntryAsync(
            Constants.AuditGlobalTenantId,
            "DELETE",
            "CustomsArchiveEntry",
            $"{partitionKey}/{archiveRowKey}",
            requestCtx.UserPrincipalName,
            new Dictionary<string, string>
            {
                { "TenantId", normalizedTenantId },
                { "HistoryRowKey", historyRowKey },
                { "ArchiveRowKey", archiveRowKey },
            });

        _logger.LogWarning(
            "CustomsArchive entry deleted by {Upn}: tenant={Tenant} history={History} archive={Archive}",
            requestCtx.UserPrincipalName, normalizedTenantId, historyRowKey, archiveRowKey);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true });
        return response;
    }

    /// <summary>
    /// DELETE /api/global/customs-archive/{tenantId}/{historyRowKey} — bulk-delete every
    /// entry of one offboarding run (after the operator has reviewed them). Audit-logged
    /// under <see cref="Constants.AuditGlobalTenantId"/> with the deleted count.
    /// </summary>
    [Function("CustomsArchiveDeleteRun")]
    public async Task<HttpResponseData> DeleteRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete",
            Route = "global/customs-archive/{tenantId}/{historyRowKey}")] HttpRequestData req,
        string tenantId,
        string historyRowKey,
        FunctionContext context)
    {
        var requestCtx = context.GetRequestContext();
        var normalizedTenantId = tenantId.ToLowerInvariant();

        var deleted = await _archive.DeleteRunAsync(normalizedTenantId, historyRowKey);

        await _maintenance.LogAuditEntryAsync(
            Constants.AuditGlobalTenantId,
            "DELETE",
            "CustomsArchiveRun",
            $"{normalizedTenantId}/{historyRowKey}",
            requestCtx.UserPrincipalName,
            new Dictionary<string, string>
            {
                { "TenantId", normalizedTenantId },
                { "HistoryRowKey", historyRowKey },
                { "DeletedCount", deleted.ToString() },
            });

        _logger.LogWarning(
            "CustomsArchive run deleted by {Upn}: tenant={Tenant} history={History} deleted={Count}",
            requestCtx.UserPrincipalName, normalizedTenantId, historyRowKey, deleted);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, deleted });
        return response;
    }

    private static string TruncatePreview(string s, int maxLength)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Length <= maxLength) return s;
        return s.Substring(0, maxLength) + "…";
    }

    public class RunSummary
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string HistoryRowKey { get; set; } = string.Empty;
        public System.DateTime ArchivedAt { get; set; }
        public int GatherRulesCount { get; set; }
        public int AnalyzeRulesCount { get; set; }
        public int ImeLogPatternsCount { get; set; }
    }

    public class EntrySummary
    {
        public string PartitionKey { get; set; } = string.Empty;
        public string RowKey { get; set; } = string.Empty;
        public string OriginalTable { get; set; } = string.Empty;
        public string OriginalRowKey { get; set; } = string.Empty;
        public System.DateTime ArchivedAt { get; set; }
        public string EntityJsonPreview { get; set; } = string.Empty;
    }
}
