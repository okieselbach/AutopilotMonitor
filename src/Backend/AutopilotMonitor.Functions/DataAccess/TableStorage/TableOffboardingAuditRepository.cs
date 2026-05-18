using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table-storage implementation of <see cref="IOffboardingAuditRepository"/>. Stores all
    /// three offboarding PartitionKey patterns (Marker / History / ByTenant) in the single
    /// <c>OffboardingAudit</c> table.
    /// <para>
    /// All writes are fail-loud — when an Azure storage call throws, exceptions propagate to
    /// the caller (queue worker), which owns retry/poison semantics via the queue SDK. This is
    /// intentionally the OPPOSITE of <see cref="TableMaintenanceRepository"/>'s fail-soft
    /// helpers; offboarding cannot tolerate silent failures during gate/audit writes.
    /// </para>
    /// </summary>
    public class TableOffboardingAuditRepository : IOffboardingAuditRepository
    {
        private readonly TableClient _tableClient;
        private readonly ILogger<TableOffboardingAuditRepository> _logger;

        public TableOffboardingAuditRepository(
            TableStorageService storage,
            ILogger<TableOffboardingAuditRepository> logger)
        {
            _tableClient = storage.GetTableClient(Constants.TableNames.OffboardingAudit);
            _logger = logger;
        }

        // ── Marker ──────────────────────────────────────────────────────────────

        public async Task<OffboardingMarkerEntry?> TryGetMarkerAsync(string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));

            try
            {
                var response = await _tableClient.GetEntityAsync<TableEntity>(
                    Constants.OffboardingPartitionKeys.Marker, normalizedTenantId, cancellationToken: ct);
                return MapMarker(response.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public Task InsertMarkerAsync(OffboardingMarkerEntry marker, CancellationToken ct = default)
        {
            EnsureMarkerShape(marker);
            return _tableClient.AddEntityAsync(StoreMarker(marker), ct);
        }

        public Task UpsertMarkerAsync(OffboardingMarkerEntry marker, CancellationToken ct = default)
        {
            EnsureMarkerShape(marker);
            return _tableClient.UpsertEntityAsync(StoreMarker(marker), TableUpdateMode.Replace, ct);
        }

        public async Task DeleteMarkerAsync(string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));

            try
            {
                await _tableClient.DeleteEntityAsync(
                    Constants.OffboardingPartitionKeys.Marker, normalizedTenantId, ETag.All, ct);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Idempotent — already gone.
            }
        }

        public async IAsyncEnumerable<OffboardingMarkerEntry> QueryMarkersAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var filter = $"PartitionKey eq '{Constants.OffboardingPartitionKeys.Marker}'";
            await foreach (var entity in _tableClient.QueryAsync<TableEntity>(filter, cancellationToken: ct))
            {
                yield return MapMarker(entity);
            }
        }

        // ── History ─────────────────────────────────────────────────────────────

        public Task InsertHistoryAsync(OffboardingHistoryEntry history, CancellationToken ct = default)
        {
            EnsureHistoryShape(history);
            return _tableClient.AddEntityAsync(StoreHistory(history), ct);
        }

        public async Task<OffboardingHistoryEntry?> TryGetHistoryAsync(string historyRowKey, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(historyRowKey)) throw new ArgumentException("historyRowKey required", nameof(historyRowKey));

            try
            {
                var response = await _tableClient.GetEntityAsync<TableEntity>(
                    Constants.OffboardingPartitionKeys.History, historyRowKey, cancellationToken: ct);
                return MapHistory(response.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public Task UpsertHistoryAsync(OffboardingHistoryEntry history, CancellationToken ct = default)
        {
            EnsureHistoryShape(history);
            return _tableClient.UpsertEntityAsync(StoreHistory(history), TableUpdateMode.Replace, ct);
        }

        // ── ByTenant Pointer ────────────────────────────────────────────────────

        public async Task<(OffboardingByTenantPointer? Pointer, string? ETag)> TryGetByTenantPointerAsync(
            string normalizedTenantId, CancellationToken ct = default)
        {
            SecurityValidator.EnsureValidGuid(normalizedTenantId, nameof(normalizedTenantId));

            try
            {
                var response = await _tableClient.GetEntityAsync<TableEntity>(
                    Constants.OffboardingPartitionKeys.ByTenant, normalizedTenantId, cancellationToken: ct);
                return (MapByTenantPointer(response.Value), response.Value.ETag.ToString());
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return (null, null);
            }
        }

        public Task InsertByTenantPointerAsync(OffboardingByTenantPointer pointer, CancellationToken ct = default)
        {
            EnsurePointerShape(pointer);
            return _tableClient.AddEntityAsync(StoreByTenantPointer(pointer), ct);
        }

        public Task UpdateByTenantPointerWithEtagAsync(
            OffboardingByTenantPointer pointer, string ifMatchEtag, CancellationToken ct = default)
        {
            EnsurePointerShape(pointer);
            if (string.IsNullOrEmpty(ifMatchEtag)) throw new ArgumentException("ifMatchEtag required", nameof(ifMatchEtag));

            var entity = StoreByTenantPointer(pointer);
            entity.ETag = new ETag(ifMatchEtag);
            // TableUpdateMode.Replace + non-default ETag → conditional replace. Mismatch → 412.
            return _tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, ct);
        }

        // ── Validation helpers ──────────────────────────────────────────────────

        private static void EnsureMarkerShape(OffboardingMarkerEntry marker)
        {
            if (marker == null) throw new ArgumentNullException(nameof(marker));
            if (marker.PartitionKey != Constants.OffboardingPartitionKeys.Marker)
                throw new ArgumentException($"Marker PartitionKey must be '{Constants.OffboardingPartitionKeys.Marker}', got '{marker.PartitionKey}'", nameof(marker));
            SecurityValidator.EnsureValidGuid(marker.RowKey, $"{nameof(marker)}.RowKey");
            SecurityValidator.EnsureValidGuid(marker.TenantId, $"{nameof(marker)}.TenantId");
        }

        private static void EnsureHistoryShape(OffboardingHistoryEntry history)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            if (history.PartitionKey != Constants.OffboardingPartitionKeys.History)
                throw new ArgumentException($"History PartitionKey must be '{Constants.OffboardingPartitionKeys.History}', got '{history.PartitionKey}'", nameof(history));
            if (string.IsNullOrEmpty(history.RowKey)) throw new ArgumentException("History RowKey required", nameof(history));
            SecurityValidator.EnsureValidGuid(history.TenantId, $"{nameof(history)}.TenantId");
        }

        private static void EnsurePointerShape(OffboardingByTenantPointer pointer)
        {
            if (pointer == null) throw new ArgumentNullException(nameof(pointer));
            if (pointer.PartitionKey != Constants.OffboardingPartitionKeys.ByTenant)
                throw new ArgumentException($"Pointer PartitionKey must be '{Constants.OffboardingPartitionKeys.ByTenant}', got '{pointer.PartitionKey}'", nameof(pointer));
            SecurityValidator.EnsureValidGuid(pointer.RowKey, $"{nameof(pointer)}.RowKey");
            SecurityValidator.EnsureValidGuid(pointer.TenantId, $"{nameof(pointer)}.TenantId");
            if (string.IsNullOrEmpty(pointer.LatestHistoryRowKey))
                throw new ArgumentException("Pointer.LatestHistoryRowKey required", nameof(pointer));
        }

        // ── Store / Map helpers (memory: feedback_table_storage_serialization) ──

        private static TableEntity StoreMarker(OffboardingMarkerEntry m) => new(m.PartitionKey, m.RowKey)
        {
            ["TenantId"] = m.TenantId,
            ["OffboardingHistoryRowKey"] = m.OffboardingHistoryRowKey,
            ["InitiatedAt"] = m.InitiatedAt,
            ["InitiatedBy"] = m.InitiatedBy,
            ["Status"] = m.Status,
            ["CompletedAt"] = m.CompletedAt,
            ["FailedAt"] = m.FailedAt,
            ["FailedPhase"] = m.FailedPhase,
        };

        private static OffboardingMarkerEntry MapMarker(TableEntity e) => new()
        {
            PartitionKey = e.PartitionKey,
            RowKey = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            OffboardingHistoryRowKey = e.GetString("OffboardingHistoryRowKey") ?? string.Empty,
            InitiatedAt = e.GetDateTime("InitiatedAt") ?? default,
            InitiatedBy = e.GetString("InitiatedBy") ?? string.Empty,
            Status = e.GetString("Status") ?? "Initiated",
            CompletedAt = e.GetDateTime("CompletedAt"),
            FailedAt = e.GetDateTime("FailedAt"),
            FailedPhase = e.GetString("FailedPhase"),
        };

        private static TableEntity StoreHistory(OffboardingHistoryEntry h) => new(h.PartitionKey, h.RowKey)
        {
            ["TenantId"] = h.TenantId,
            ["DomainName"] = h.DomainName,
            ["InitiatedBy"] = h.InitiatedBy,
            ["OffboardedAt"] = h.OffboardedAt,
            ["CompletedAt"] = h.CompletedAt,
            ["EarliestProcessingAt"] = h.EarliestProcessingAt,
            ["Status"] = h.Status,
            ["DeletedRowCountsJson"] = h.DeletedRowCountsJson,
            ["TotalRowsDeleted"] = h.TotalRowsDeleted,
            ["DeletedBlobCount"] = h.DeletedBlobCount,
            ["CascadeSessionsEnqueued"] = h.CascadeSessionsEnqueued,
            ["ErrorMessage"] = h.ErrorMessage,
            ["RetryCount"] = h.RetryCount,
            ["DrainCompletedAt"] = h.DrainCompletedAt,
            ["EnumerationStartedAt"] = h.EnumerationStartedAt,
            ["EnumerationCompletedBeforeUpload"] = h.EnumerationCompletedBeforeUpload,
            ["CustomGatherRulesArchived"] = h.CustomGatherRulesArchived,
            ["CustomAnalyzeRulesArchived"] = h.CustomAnalyzeRulesArchived,
            ["ImeLogPatternOverridesArchived"] = h.ImeLogPatternOverridesArchived,
            ["ReonboardedAt"] = h.ReonboardedAt,
            ["ReonboardedBy"] = h.ReonboardedBy,
            ["CustomsAutoWipedOnReonboard"] = h.CustomsAutoWipedOnReonboard,
        };

        private static OffboardingHistoryEntry MapHistory(TableEntity e) => new()
        {
            PartitionKey = e.PartitionKey,
            RowKey = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            DomainName = e.GetString("DomainName") ?? string.Empty,
            InitiatedBy = e.GetString("InitiatedBy") ?? string.Empty,
            OffboardedAt = e.GetDateTime("OffboardedAt") ?? default,
            CompletedAt = e.GetDateTime("CompletedAt"),
            EarliestProcessingAt = e.GetDateTime("EarliestProcessingAt"),
            Status = e.GetString("Status") ?? "Initiated",
            DeletedRowCountsJson = e.GetString("DeletedRowCountsJson"),
            TotalRowsDeleted = e.GetInt32("TotalRowsDeleted"),
            DeletedBlobCount = e.GetInt32("DeletedBlobCount"),
            CascadeSessionsEnqueued = e.GetInt32("CascadeSessionsEnqueued"),
            ErrorMessage = e.GetString("ErrorMessage"),
            RetryCount = e.GetInt32("RetryCount") ?? 0,
            DrainCompletedAt = e.GetDateTime("DrainCompletedAt"),
            EnumerationStartedAt = e.GetDateTime("EnumerationStartedAt"),
            EnumerationCompletedBeforeUpload = e.GetDateTime("EnumerationCompletedBeforeUpload"),
            CustomGatherRulesArchived = e.GetInt32("CustomGatherRulesArchived"),
            CustomAnalyzeRulesArchived = e.GetInt32("CustomAnalyzeRulesArchived"),
            ImeLogPatternOverridesArchived = e.GetInt32("ImeLogPatternOverridesArchived"),
            ReonboardedAt = e.GetDateTime("ReonboardedAt"),
            ReonboardedBy = e.GetString("ReonboardedBy"),
            CustomsAutoWipedOnReonboard = e.GetInt32("CustomsAutoWipedOnReonboard"),
        };

        private static TableEntity StoreByTenantPointer(OffboardingByTenantPointer p) => new(p.PartitionKey, p.RowKey)
        {
            ["TenantId"] = p.TenantId,
            ["LatestHistoryRowKey"] = p.LatestHistoryRowKey,
            ["LatestStatus"] = p.LatestStatus,
            ["LatestUpdatedAt"] = p.LatestUpdatedAt,
            ["OffboardCount"] = p.OffboardCount,
        };

        private static OffboardingByTenantPointer MapByTenantPointer(TableEntity e) => new()
        {
            PartitionKey = e.PartitionKey,
            RowKey = e.RowKey,
            TenantId = e.GetString("TenantId") ?? string.Empty,
            LatestHistoryRowKey = e.GetString("LatestHistoryRowKey") ?? string.Empty,
            LatestStatus = e.GetString("LatestStatus") ?? "Initiated",
            LatestUpdatedAt = e.GetDateTime("LatestUpdatedAt") ?? default,
            OffboardCount = e.GetInt32("OffboardCount") ?? 1,
        };
    }
}
