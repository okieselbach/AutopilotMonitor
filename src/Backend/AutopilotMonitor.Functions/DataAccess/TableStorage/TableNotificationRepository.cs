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
    /// Table Storage implementation of INotificationRepository.
    /// Manages GlobalNotifications and SessionReports tables.
    /// Uses inverted-tick RowKeys to sort newest-first.
    /// </summary>
    public class TableNotificationRepository : INotificationRepository
    {
        private readonly TableClient _notificationsTableClient;
        private readonly TableClient _reportsTableClient;
        private readonly ILogger<TableNotificationRepository> _logger;

        public TableNotificationRepository(
            TableStorageService storage,
            ILogger<TableNotificationRepository> logger)
        {
            _logger = logger;
            _notificationsTableClient = storage.GetTableClient(Constants.TableNames.GlobalNotifications);
            _reportsTableClient = storage.GetTableClient(Constants.TableNames.SessionReports);
        }

        // --- Global Notifications ---

        public async Task<bool> AddNotificationAsync(GlobalNotification notification)
        {
            try
            {
                var invertedTicks = RowKeyCodec.InvertedTicks(notification.CreatedAt);
                var notificationId = notification.NotificationId;
                if (string.IsNullOrEmpty(notificationId))
                    notificationId = Guid.NewGuid().ToString("N")[..12];

                var entity = new TableEntity("notifications", $"{invertedTicks}_{notificationId}")
                {
                    ["NotificationId"] = notificationId,
                    ["Type"] = notification.Type ?? "info",
                    ["Title"] = notification.Title ?? string.Empty,
                    ["Message"] = notification.Message ?? string.Empty,
                    ["Href"] = notification.Href,
                    ["CreatedAt"] = notification.CreatedAt,
                    ["CreatedBy"] = notification.CreatedBy ?? string.Empty,
                    ["Dismissed"] = notification.IsDismissed
                };

                await _notificationsTableClient.UpsertEntityAsync(entity);
                _logger.LogInformation("Global notification stored: {NotificationId} ({Type})", notificationId, notification.Type);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store global notification ({Type}: {Title})", notification.Type, notification.Title);
                return false;
            }
        }

        public async Task<List<GlobalNotification>> GetNotificationsAsync(int maxResults = 50)
        {
            try
            {
                var notifications = new List<GlobalNotification>();
                var count = 0;

                await foreach (var entity in _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'notifications'"))
                {
                    var dismissed = entity.GetBoolean("Dismissed") ?? false;
                    if (dismissed) continue;

                    notifications.Add(MapToGlobalNotification(entity));
                    count++;
                    if (count >= maxResults) break;
                }

                return notifications;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogWarning("GlobalNotifications table not found — returning empty list");
                return new List<GlobalNotification>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve global notifications");
                return new List<GlobalNotification>();
            }
        }

        public async Task<bool> DismissNotificationAsync(string notificationId, string dismissedBy)
        {
            try
            {
                await foreach (var entity in _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'notifications'"))
                {
                    if (entity.GetString("NotificationId") == notificationId)
                    {
                        entity["Dismissed"] = true;
                        entity["DismissedBy"] = dismissedBy;
                        entity["DismissedAt"] = DateTime.UtcNow;
                        await _notificationsTableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                        _logger.LogInformation("Global notification dismissed: {NotificationId}", notificationId);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dismiss global notification {NotificationId}", notificationId);
                return false;
            }
        }

        public async Task<int> DismissAllNotificationsAsync()
        {
            try
            {
                var count = 0;

                await foreach (var entity in _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'notifications'"))
                {
                    var dismissed = entity.GetBoolean("Dismissed") ?? false;
                    if (dismissed) continue;

                    entity["Dismissed"] = true;
                    entity["DismissedAt"] = DateTime.UtcNow;
                    await _notificationsTableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
                    count++;
                }

                _logger.LogInformation("Dismissed {Count} global notifications", count);
                return count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dismiss all global notifications");
                return 0;
            }
        }

        public async Task<int> DeleteNotificationsByRetentionAsync(DateTime dismissedCutoffUtc, DateTime unreadCutoffUtc)
        {
            try
            {
                // Hybrid retention: a dismissed row drops 30 days after DISMISSAL (DismissedAt), unread
                // (still-actionable) rows survive until the long creation-age cutoff so an unacknowledged
                // admin warning is never silently lost inside the dismiss window. The long clause is the
                // catch-all that bounds the table regardless of dismiss state. All global notifications
                // live in one "notifications" partition; the SDK filters server-side.
                var filter = "PartitionKey eq 'notifications' and " +
                    NotificationRetentionFilter.BuildPredicate(dismissedCutoffUtc, unreadCutoffUtc);
                var query = _notificationsTableClient.QueryAsync<TableEntity>(
                    filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    try
                    {
                        await _notificationsTableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete global notification {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }

                if (deleted > 0)
                    _logger.LogInformation(
                        "Deleted {Count} global notifications (dismissed<{DismissedCutoff:yyyy-MM-dd}, any<{UnreadCutoff:yyyy-MM-dd})",
                        deleted, dismissedCutoffUtc, unreadCutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old global notifications");
                return 0;
            }
        }

        // --- Session Reports ---

        public async Task<bool> StoreSessionReportMetadataAsync(SessionReportMetadata metadata)
        {
            try
            {
                var invertedTicks = RowKeyCodec.InvertedTicks(metadata.SubmittedAt);
                var entity = new TableEntity("reports", $"{invertedTicks}_{metadata.ReportId}")
                {
                    ["ReportId"] = metadata.ReportId ?? string.Empty,
                    ["TenantId"] = metadata.TenantId ?? string.Empty,
                    ["SessionId"] = metadata.SessionId ?? string.Empty,
                    ["Comment"] = metadata.Comment ?? string.Empty,
                    ["Email"] = metadata.Email ?? string.Empty,
                    ["BlobName"] = metadata.BlobName ?? string.Empty,
                    ["SubmittedBy"] = metadata.SubmittedBy ?? string.Empty,
                    ["SubmittedAt"] = metadata.SubmittedAt,
                    ["ReportType"] = string.IsNullOrEmpty(metadata.ReportType) ? ReportTypes.Session : metadata.ReportType
                };

                // Diagnostics-copy columns only exist on rows where the copy was requested —
                // legacy-shaped rows stay untouched and map back to null.
                if (metadata.DiagnosticsBlobName != null)
                    entity["DiagnosticsBlobName"] = metadata.DiagnosticsBlobName;
                if (metadata.DiagnosticsCopyStatus != null)
                    entity["DiagnosticsCopyStatus"] = metadata.DiagnosticsCopyStatus;

                await _reportsTableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store session report metadata {ReportId}", metadata.ReportId);
                return false;
            }
        }

        public async Task<List<SessionReportMetadata>> GetSessionReportsAsync(string? tenantId = null)
        {
            var results = new List<SessionReportMetadata>();
            var filter = BuildSessionReportFilter(tenantId);
            try
            {
                await foreach (var entity in _reportsTableClient.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToSessionReportMetadata(entity));
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("SessionReports table does not exist yet, returning empty list");
            }

            return results;
        }

        public async Task<RawPage<SessionReportMetadata>> GetSessionReportsPageAsync(
            string? tenantId, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            try
            {
                var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                    client: _reportsTableClient,
                    filter: BuildSessionReportFilter(tenantId),
                    pageSize: pageSize,
                    continuation: continuation);

                var page = new List<SessionReportMetadata>(entities.Count);
                foreach (var entity in entities) page.Add(MapToSessionReportMetadata(entity));
                // RowKey is inverted-tick of SubmittedAt — entities arrive newest-first
                // already; no client-side resort needed.
                return new RawPage<SessionReportMetadata>(page, nextRawToken);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("SessionReports table does not exist yet, returning empty page");
                return RawPage<SessionReportMetadata>.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get session reports page");
                return RawPage<SessionReportMetadata>.Empty;
            }
        }

        private static string BuildSessionReportFilter(string? tenantId)
        {
            // All reports live in the single "reports" partition; tenantId is a
            // property the SDK can filter server-side.
            var filter = "PartitionKey eq 'reports'";
            if (!string.IsNullOrEmpty(tenantId))
            {
                filter += $" and TenantId eq '{tenantId!.Replace("'", "''")}'";
            }
            return filter;
        }

        public async Task<SessionReportMetadata?> GetSessionReportAsync(string reportId)
        {
            try
            {
                await foreach (var entity in _reportsTableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'reports'"))
                {
                    if (entity.GetString("ReportId") == reportId)
                    {
                        return MapToSessionReportMetadata(entity);
                    }
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug("SessionReports table does not exist yet");
            }

            return null;
        }

        public async Task<bool> UpdateSessionReportAdminNoteAsync(string reportId, string adminNote)
        {
            TableEntity? found = null;
            await foreach (var entity in _reportsTableClient.QueryAsync<TableEntity>(
                filter: $"PartitionKey eq 'reports'"))
            {
                if (entity.GetString("ReportId") == reportId)
                {
                    found = entity;
                    break;
                }
            }

            if (found == null)
            {
                _logger.LogWarning("UpdateAdminNote: report {ReportId} not found", reportId);
                return false;
            }

            found["AdminNote"] = adminNote ?? string.Empty;
            await _reportsTableClient.UpsertEntityAsync(found, TableUpdateMode.Merge);

            _logger.LogInformation("Updated AdminNote for report {ReportId}", reportId);
            return true;
        }

        // --- Helpers ---

        private static GlobalNotification MapToGlobalNotification(TableEntity entity)
        {
            return new GlobalNotification
            {
                NotificationId = entity.GetString("NotificationId") ?? string.Empty,
                Type = entity.GetString("Type") ?? "info",
                Title = entity.GetString("Title") ?? string.Empty,
                Message = entity.GetString("Message") ?? string.Empty,
                Href = entity.GetString("Href"),
                CreatedAt = entity.GetDateTimeOffset("CreatedAt")?.UtcDateTime ?? DateTime.MinValue,
                CreatedBy = entity.GetString("CreatedBy") ?? string.Empty,
                IsDismissed = entity.GetBoolean("Dismissed") ?? false,
                DismissedBy = entity.GetString("DismissedBy"),
                DismissedAt = entity.GetDateTimeOffset("DismissedAt")?.UtcDateTime
            };
        }

        private static SessionReportMetadata MapToSessionReportMetadata(TableEntity entity)
        {
            // Legacy rows written before the ReportType column existed default to "session".
            var reportType = entity.GetString("ReportType");
            return new SessionReportMetadata
            {
                ReportId = entity.GetString("ReportId") ?? string.Empty,
                TenantId = entity.GetString("TenantId") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                Comment = entity.GetString("Comment") ?? string.Empty,
                Email = entity.GetString("Email") ?? string.Empty,
                BlobName = entity.GetString("BlobName") ?? string.Empty,
                SubmittedBy = entity.GetString("SubmittedBy") ?? string.Empty,
                SubmittedAt = entity.GetDateTimeOffset("SubmittedAt")?.UtcDateTime ?? DateTime.MinValue,
                AdminNote = entity.GetString("AdminNote") ?? string.Empty,
                ReportType = string.IsNullOrEmpty(reportType) ? ReportTypes.Session : reportType,
                DiagnosticsBlobName = NullIfEmpty(entity.GetString("DiagnosticsBlobName")),
                DiagnosticsCopyStatus = NullIfEmpty(entity.GetString("DiagnosticsCopyStatus"))
            };
        }

        private static string? NullIfEmpty(string? value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
